using DieselTools_ExileAPI;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using System.Drawing.Text;
using System.Security.Cryptography.X509Certificates;
using SVector2 = System.Numerics.Vector2;

namespace BetterMapIcons {

    public sealed class IconRenderer : PluginModule
    {

        public IconRenderer(Plugin plugin) : base(plugin) { }

        private const float CameraAngle = 38.7f * MathF.PI / 180;
        private static readonly float CameraAngleCos = MathF.Cos(CameraAngle);
        private static readonly float CameraAngleSin = MathF.Sin(CameraAngle);

        private CachedValue<List<MapIcon>> RenderedMapIconsCache;

        public void Initialise() {

            RenderedMapIconsCache = new TimeCache<List<MapIcon>>(() => {
                var entitySource = Settings.DrawCachedEntities
                    ? GameController?.EntityListWrapper.Entities
                    : GameController?.EntityListWrapper?.OnlyValidEntities;
                var baseIcons = entitySource?.Select(x => x.GetHudComponent<MapIcon>())
                    .Where(icon => icon != null)
                    .OrderBy(x => x.Priority)
                    .ToList();
                return baseIcons ?? [];
            }, Settings.IconListUpdatePeriod);

        }
        public void Tick() { }

        public void Render() {
            var ingameUi = GameController.IngameState.IngameUi;
            if (ingameUi == null || !ingameUi.IsVisibleLocal) return;

            var mapCenter = new SVector2();
            var mapScale = 1.0f;
            bool? largeMapOpen = null;

            // Render icons on the map based on the current map state
            var smallMiniMap = ingameUi.Map.SmallMiniMap;
            var largeMapWindow = ingameUi.Map.LargeMap;

            if (smallMiniMap.IsValid && smallMiniMap.IsVisibleLocal) {
                var mapRect = smallMiniMap.GetClientRectCache;
                mapCenter = mapRect.Center.ToVector2Num();
                largeMapOpen = false;
                mapScale = smallMiniMap.MapScale;
            }
            else if (ingameUi.Map.LargeMap.IsVisibleLocal) {
                mapCenter = largeMapWindow.MapCenter;
                largeMapOpen = true;
                mapScale = largeMapWindow.MapScale;
            }
            // check game states
            if (largeMapOpen == null || !GameController.InGame) return;
            if (!Settings.DrawOverFullscreenPanels && ingameUi.FullscreenPanels.Any(x => x.IsVisible) || !Settings.DrawOverLargePanels && ingameUi.LargePanels.Any(x => x.IsVisible)) return;
            if (largeMapWindow == null) return;

            // Get player position and height
            var playerRender = GameController?.Player?.GetComponent<Render>();
            if (playerRender == null) return;
            var view = new MapView(mapCenter, mapScale, playerRender.PosNum.WorldToGrid(), -playerRender.UnclampedHeight);

            DrawTerrain(view, largeMapOpen.Value, ingameUi);

            if (!Settings.DrawOnMinimap && largeMapOpen != true) return;
            // Get the cached icons list
            if (RenderedMapIconsCache == null) return;
            var renderedMapIcons = RenderedMapIconsCache.Value;
            if (renderedMapIcons == null) return;

            // Render each icon on the map
            foreach (var mapIcon in renderedMapIcons) {

                if (mapIcon?.Entity == null) continue;
                if (!mapIcon.Show()) continue;
                if (mapIcon.Settings == null) {
                    mapIcon.Settings = Settings.GetIconSettings(mapIcon.Type);
                    if (mapIcon.Settings == null) continue;
                }

                // Only things with Life can die: chests always report IsAlive false and would never be drawn
                if (mapIcon.Settings.Check_IsAlive && mapIcon.Entity.HasComponent<Life>() && !mapIcon.Entity.IsAlive) continue;
                if (mapIcon.Settings.Check_IsOpened && mapIcon.Entity.IsOpened) continue;

                var iconFileName = Plugin.IconAtlas.Name;
                var iconSize = 0;
                var iconColor = SharpDX.Color.White;
                var iconUV = new SharpDX.RectangleF(); // Default UV coordinates

                // get icon position
                var iconGridPos = mapIcon.GridPosition();
                var iconPosition = view.GridToScreen(iconGridPos, GameController.IngameState.Data.GetTerrainHeightAt(iconGridPos));

                // icon rendering
                if (mapIcon.Renderer == MapIconRenderers.IngameIcon) {
                    if (mapIcon.Settings.DrawState == IconDrawStates.Off || (mapIcon.Settings.DrawState == IconDrawStates.Ranged && mapIcon.Entity.IsValid)) continue;

                    iconFileName = mapIcon.InGameTexture.FileName;
                    iconSize = (int)mapIcon.InGameTexture.Size;
                    iconUV = mapIcon.InGameTexture.UV;
                    mapIcon.DrawRect = GetIconPositionRect(iconSize, iconPosition, ingameUi);
                    if (mapIcon.DrawRect == null) continue;
                    Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                    if (mapIcon.Settings.DrawName) Graphics.DrawText(mapIcon.Name, iconPosition.Translate(0, 0), FontAlign.Center);

                    continue;
                }

                if (!mapIcon.Settings.Draw) continue;
                iconSize = mapIcon.Settings.Size;
                iconUV = Plugin.IconAtlas.GetIconUV(mapIcon.Settings.Index);
                mapIcon.DrawRect = GetIconPositionRect(iconSize, iconPosition, ingameUi);
                if (mapIcon.DrawRect == null) continue;

                var lifeComponent = mapIcon.Entity?.GetComponent<Life>();

                switch (mapIcon.Renderer) {
                    case MapIconRenderers.Default:
                        iconColor = mapIcon.Hidden() ? mapIcon.Settings.HiddenTint : mapIcon.Settings.Tint;
                        // icon
                        Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                        // text
                        if (mapIcon.Settings.DrawName) Graphics.DrawText(mapIcon.Name, iconPosition.Translate(0, 0), FontAlign.Center);
                        break;
                    case MapIconRenderers.Monster:
                        iconColor = mapIcon.Hidden() ? mapIcon.Settings.HiddenTint : mapIcon.Settings.Tint;
                        // icon
                        if (mapIcon.Settings.AnimateLife && lifeComponent != null && lifeComponent.HPPercentage < 0.875f) {
                            // use switch to get life percentages in 12.5% increments
                            var iconIndex = mapIcon.Settings.Index;
                            switch (lifeComponent.HPPercentage) {
                                case > 0.75f: iconIndex += 1; break;
                                case > 0.625f: iconIndex += 2; break;
                                case > 0.5f: iconIndex += 3; break;
                                case > 0.375f: iconIndex += 4; break;
                                case > 0.25f: iconIndex += 5; break;
                                case > 0.125f: iconIndex += 6; break;
                                default: iconIndex += 7; break; // Handles 12.5% and below
                            }
                            iconUV = Plugin.IconAtlas.GetIconUV(iconIndex);
                            Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                        }
                        else
                            Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                        // text 
                        if (mapIcon.Settings.DrawName) Graphics.DrawText(mapIcon.Name, iconPosition.Translate(0, 0), FontAlign.Center);
                        break;
                    case MapIconRenderers.Chest:
                        iconColor = mapIcon.Settings.Tint;

                        Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                        break;
                    case MapIconRenderers.Friendly:
                        iconColor = mapIcon.Hidden() ? mapIcon.Settings.HiddenTint : mapIcon.Settings.Tint;

                        // icon
                        if (mapIcon.Settings.AnimateLife && lifeComponent != null && lifeComponent.HPPercentage < 0.875f) {
                            // use switch to get life percentages in 12.5% increments
                            var iconIndex = mapIcon.Settings.Index;
                            switch (lifeComponent.HPPercentage) {
                                case > 0.75f:  iconIndex += 1; break;
                                case > 0.625f: iconIndex += 2; break;
                                case > 0.5f:   iconIndex += 3; break;
                                case > 0.375f: iconIndex += 4; break;
                                case > 0.25f:  iconIndex += 5; break;
                                case > 0.125f: iconIndex += 6; break;
                                default: iconIndex += 7; break; // Handles 12.5% and below
                            }
                            iconUV = Plugin.IconAtlas.GetIconUV(iconIndex);
                            Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                        }
                        else 
                            Graphics.DrawImage(iconFileName, mapIcon.DrawRect.Value, iconUV, iconColor);
                        // text 
                        string name = mapIcon.Settings.DrawName ? mapIcon.Name : null;
                        string lifeText = null;
                        if (mapIcon.Settings.DrawHealth) {
                            if (lifeComponent != null) {
                                lifeText = $"{lifeComponent.CurHP}";
                            }
                        }
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(lifeText)) {
                            Graphics.DrawText($"{name} {lifeText}", iconPosition.Translate(0, 0), FontAlign.Center);
                        }
                        else if (!string.IsNullOrEmpty(name)) {
                            Graphics.DrawText(name, iconPosition.Translate(0, 0), FontAlign.Center);
                        }
                        else if (!string.IsNullOrEmpty(lifeText)) {
                            Graphics.DrawText(lifeText, iconPosition.Translate(0, 0), FontAlign.Center);
                        }
                        break;
                    default:
                        break;
                }
            }
        }

        // Projects grid positions onto the visible map; built once per frame from the player's position.
        private readonly struct MapView {
            private readonly SVector2 _center;
            private readonly float _scale;
            private readonly SVector2 _playerGridPos;
            private readonly float _playerHeight;

            public MapView(SVector2 center, float scale, SVector2 playerGridPos, float playerHeight) {
                _center = center;
                _scale = scale;
                _playerGridPos = playerGridPos;
                _playerHeight = playerHeight;
            }

            // terrainHeight is the world height at gridPos; pass 0 for the terrain texture, which has heights baked in.
            public SVector2 GridToScreen(SVector2 gridPos, float terrainHeight) {
                var delta = gridPos - _playerGridPos;
                var deltaZ = (_playerHeight + terrainHeight) * PoeMapExtension.WorldToGridConversion;
                return _center + _scale * SVector2.Multiply(new SVector2(delta.X - delta.Y, deltaZ - (delta.X + delta.Y)), new SVector2(CameraAngleCos, CameraAngleSin));
            }
        }

        private void DrawTerrain(MapView view, bool largeMapOpen, IngameUIElements ingameUi) {
            if (!Settings.DrawTerrain) return;
            if (largeMapOpen ? !Settings.DrawTerrainOnLargeMap : !Settings.DrawTerrainOnMinimap) return;
            if (Plugin.MapTerrain.AreaSize is not { } areaSize || !Graphics.HasImage(MapTerrain.TextureName)) return;

            using var clip = largeMapOpen
                ? Graphics.BeginRectClip(GetLargeMapClipRect(ingameUi))
                : Graphics.MapSurfaceClip(VisibleSubMap.Any);
            Graphics.DrawQuad(Graphics.GetTextureId(MapTerrain.TextureName),
                view.GridToScreen(SVector2.Zero, 0),
                view.GridToScreen(new SVector2(areaSize.X, 0), 0),
                view.GridToScreen(areaSize, 0),
                view.GridToScreen(new SVector2(0, areaSize.Y), 0));
        }

        // The large map spans the whole window; keep the terrain off the side panels the way the game does.
        private SharpDX.RectangleF GetLargeMapClipRect(IngameUIElements ingameUi) {
            var rect = GameController.Window.GetWindowRectangle() with { Location = SharpDX.Vector2.Zero };
            if (ingameUi.OpenRightPanel.IsVisible) rect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
            if (ingameUi.OpenLeftPanel.IsVisible) rect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;
            return rect;
        }

        public SharpDX.RectangleF? GetIconPositionRect(int iconSize, SVector2 iconPosition, IngameUIElements inGameUI) {
            float halfSize = iconSize / 2;
            float iconX = iconPosition.X - halfSize;
            float iconY = iconPosition.Y - halfSize;
            if (Settings.PixelPerfectIcons) {
                iconX = MathF.Round(iconX);
                iconY = MathF.Round(iconY);
            }

            var rect = new SharpDX.RectangleF(iconX, iconY, iconSize, iconSize);

            if (inGameUI.Map.LargeMap.IsVisibleLocal == false && !inGameUI.Map.SmallMiniMap.GetClientRectCache.Contains(rect)) return null; 

            return rect;
        }







    }
}
