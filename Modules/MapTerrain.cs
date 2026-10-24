using ExileCore.Shared.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageConfiguration = SixLabors.ImageSharp.Configuration;
using SVector2 = System.Numerics.Vector2;

namespace MapIcons;

// Builds a texture of the whole area layout from the game's pathfinding grid (the same data the Radar
// plugin uses), so IconRenderer can draw the entire map under the icons, explored or not.
public sealed class MapTerrain : PluginModule {
    public MapTerrain(Plugin plugin) : base(plugin) { }

    public const string TextureName = "MapIcons_Terrain";
    private const float GridToWorld = PoeMapExtension.TileToWorldConversion / (float)PoeMapExtension.TileToGridConversion;

    private const byte Unknown = 0, Walkable = 1, Blocked = 2;

    private readonly record struct TerrainStyle(Rgba32 Outline, Rgba32 Fill, int OutlineWidth, bool HeightAdjust, bool ClearBlockades, int MaxTextureSize);
    private sealed record TerrainResult(uint AreaHash, TerrainStyle Style, SVector2 AreaSize, Image<Rgba32> Image);

    private Task _job;
    private CancellationTokenSource _jobCts;
    private uint _jobArea;
    private TerrainStyle _jobStyle;
    private TerrainResult _finished; // handed over from the worker, uploaded on the render thread

    private uint? _textureArea;
    private TerrainStyle _textureStyle;

    // Grid size of the area the uploaded texture covers, null while there is no texture for this area.
    public SVector2? AreaSize { get; private set; }

    public void AreaChange() {
        AreaSize = null;
        _textureArea = null;
        _jobCts?.Cancel();
        _job = null; // re-entering the same instance keeps its hash, so forget the job to force a rebuild
    }

    // Called every frame: uploads a finished texture and starts a rebuild when the area or style changed.
    public void Render() {
        if (!Settings.DrawTerrain) return;

        var area = GameController.Area.CurrentArea;
        if (area == null || !GameController.InGame) return;
        var areaHash = area.Hash;

        var finished = Interlocked.Exchange(ref _finished, null);
        if (finished != null) {
            using (finished.Image) {
                if (finished.AreaHash == areaHash) {
                    Graphics.LowLevel.AddOrUpdateTexture(TextureName, finished.Image);
                    _textureArea = finished.AreaHash;
                    _textureStyle = finished.Style;
                    AreaSize = finished.AreaSize;
                }
            }
        }

        var style = CurrentStyle();
        if (_textureArea == areaHash && _textureStyle == style) return;

        // One job at a time: a job for another area is cancelled, a job for a stale style is left to
        // finish (cheaper than restarting on every frame of a color drag) and the next frame catches up.
        if (_job is { IsCompleted: false }) {
            if (_jobArea != areaHash) _jobCts.Cancel();
            return;
        }
        // The last job already ran for this area and style: its result uploads next frame, or it failed
        // and retrying every frame would not help.
        if (_job != null && _jobArea == areaHash && _jobStyle == style) return;

        // Not disposing the previous source: a cancelled job may still be unwinding with its token.
        _jobCts = new CancellationTokenSource();
        _jobArea = areaHash;
        _jobStyle = style;
        var token = _jobCts.Token;
        _job = Task.Run(() => {
            try {
                var result = Build(areaHash, style, token);
                if (result != null) Interlocked.Exchange(ref _finished, result)?.Image.Dispose();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) {
                DieselTools_ExileAPI.DBugger.Log($"Map terrain build failed: {e.Message}");
            }
        }, token);
    }

    private TerrainStyle CurrentStyle() => new(
        ToRgba(Settings.TerrainOutlineColor),
        Settings.TerrainFillWalkable ? ToRgba(Settings.TerrainFillColor) : default,
        Math.Clamp(Settings.TerrainOutlineWidth, 1, 6),
        Settings.TerrainHeightAdjust,
        Settings.TerrainClearBlockades,
        Math.Clamp(Settings.TerrainMaxTextureSize, 256, 4096));

    private static Rgba32 ToRgba(SharpDX.Color c) => new(c.R, c.G, c.B, c.A);

    private TerrainResult Build(uint areaHash, TerrainStyle style, CancellationToken token) {
        var data = GameController.IngameState.Data;
        var dimensions = data.AreaDimensions;
        var pathfinding = style.ClearBlockades ? data.GetClearedPathfindingData() : data.RawPathfindingData;
        var heights = style.HeightAdjust ? data.RawTerrainHeightData : null;
        int width = dimensions.X, height = dimensions.Y;
        if (pathfinding == null || width <= 0 || height <= 0) return null;
        height = Math.Min(height, pathfinding.Length);
        token.ThrowIfCancellationRequested();

        var options = new ParallelOptions { CancellationToken = token };
        var cells = new byte[width * height];

        // Classify every cell. With height adjust each cell is shifted the way the map projection would
        // move it, so the flat texture lines up with icons drawn at their terrain height.
        Parallel.For(0, height, options, y => {
            var row = pathfinding[y];
            var heightRow = heights != null && y < heights.Length ? heights[y] : null;
            var rowWidth = Math.Min(width, row.Length);
            for (var x = 0; x < rowWidth; x++) {
                int tx = x, ty = y;
                if (heightRow is { Length: > 0 }) {
                    var offset = (int)(heightRow[Math.Min(x / 2 * 2, heightRow.Length - 1)] / GridToWorld / 2);
                    tx -= offset;
                    ty -= offset;
                    if (tx < 0 || tx >= width || ty < 0 || ty >= height) continue;
                }
                cells[ty * width + tx] = row[x] == 0 ? Blocked : Walkable;
            }
        });

        // Fill the holes the height shift leaves behind with the majority of their neighbours.
        if (heights != null) {
            Parallel.For(0, height, options, y => {
                for (var x = 0; x < width; x++) {
                    if (cells[y * width + x] != Unknown) continue;
                    int walkable = 0, blocked = 0;
                    for (var oy = -1; oy <= 1; oy++) {
                        var ny = y + oy;
                        if (ny < 0 || ny >= height) continue;
                        for (var ox = -1; ox <= 1; ox++) {
                            var nx = x + ox;
                            if (nx < 0 || nx >= width) continue;
                            var neighbour = cells[ny * width + nx];
                            if (neighbour == Walkable) walkable++;
                            else if (neighbour == Blocked) blocked++;
                        }
                    }
                    cells[y * width + x] = walkable > blocked ? Walkable : Blocked;
                }
            });
        }

        // Paint: blocked cells within OutlineWidth of a walkable cell form the outline.
        var configuration = ImageConfiguration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        var image = new Image<Rgba32>(configuration, width, height);
        try {
            var radius = style.OutlineWidth;
            var paintFill = style.Fill.A > 0;
            Parallel.For(0, height, options, y => {
                for (var x = 0; x < width; x++) {
                    var cell = cells[y * width + x];
                    if (cell == Walkable) {
                        if (paintFill) image[x, y] = style.Fill;
                        continue;
                    }
                    if (IsNearWalkable(cells, width, height, x, y, radius)) image[x, y] = style.Outline;
                }
            });
            token.ThrowIfCancellationRequested();

            if (Math.Max(width, height) > style.MaxTextureSize) {
                var scale = style.MaxTextureSize / (float)Math.Max(width, height);
                image.Mutate(c => c.Resize(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale))));
            }

            // Resizing does not honour PreferContiguousImageBuffers, and the upload needs one buffer.
            var contiguous = image.Clone(configuration);
            return new TerrainResult(areaHash, style, new SVector2(dimensions.X, dimensions.Y), contiguous);
        }
        finally {
            image.Dispose();
        }
    }

    private static bool IsNearWalkable(byte[] cells, int width, int height, int x, int y, int radius) {
        var minY = Math.Max(0, y - radius);
        var maxY = Math.Min(height - 1, y + radius);
        var minX = Math.Max(0, x - radius);
        var maxX = Math.Min(width - 1, x + radius);
        for (var ny = minY; ny <= maxY; ny++) {
            var rowStart = ny * width;
            for (var nx = minX; nx <= maxX; nx++) {
                if (cells[rowStart + nx] == Walkable) return true;
            }
        }
        return false;
    }
}
