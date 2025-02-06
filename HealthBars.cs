using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace HealthBars;

public class HealthBars : BaseSettingsPlugin<HealthBarsSettings>
{
    private const string ShadedHealthbarTexture = "healthbar.png";
    private const string FlatHealthbarTexture = "chest.png";
    private string OldConfigPath => Path.Combine(DirectoryFullName, "config", "ignored_entities.txt");
    private string NewConfigCustomPath => Path.Join(ConfigDirectory, "entityConfig.json");

    private Camera Camera => GameController.IngameState.Camera;
    private IngameUIElements IngameUi => GameController.IngameState.IngameUi;
    private Vector2 WindowRelativeSize => new Vector2(_windowRectangle.Value.Width / 2560, _windowRectangle.Value.Height / 1600);
    private string HealthbarTexture => TexturePrefix + (Settings.UseShadedTexture ? ShadedHealthbarTexture : FlatHealthbarTexture);

    private readonly ConcurrentDictionary<string, EntityTreatmentRule> _pathRuleCache = new();
    private bool _canTick = true;
    private IndividualEntityConfig _entityConfig = new IndividualEntityConfig(new SerializedIndividualEntityConfig());
    private Vector2 _oldPlayerCoord;
    private HealthBar _playerBar;
    private CachedValue<bool> _ingameUiCheckVisible;
    private CachedValue<RectangleF> _windowRectangle;

    #region DPS Tracking Fields

    // A list to hold recent damage events.
    private List<DamageEvent> _damageEvents = new List<DamageEvent>();

    // A dictionary to store the last–known HP for each boss health bar (keyed by entity address).
    private Dictionary<long, float> _lastHp = new Dictionary<long, float>();

    // The sliding time window over which we average DPS (e.g., 10 seconds).
    private readonly TimeSpan DamageWindow = TimeSpan.FromSeconds(2);

    // Global maximum DPS detected since the last area change.
    private double _maxDps = 0;

    // A simple structure to record a damage event.
    private struct DamageEvent
    {
        public DateTime Time;
        public float Damage;
    }

    #endregion

    public override void OnLoad()
    {
        CanUseMultiThreading = true;
        Graphics.InitImage(TexturePrefix + ShadedHealthbarTexture, Path.Combine(DirectoryFullName, ShadedHealthbarTexture));
        Graphics.InitImage(TexturePrefix + FlatHealthbarTexture, Path.Combine(DirectoryFullName, FlatHealthbarTexture));
    }

    public override bool Initialise()
    {
        _windowRectangle = new TimeCache<ExileCore2.Shared.RectangleF>(() =>
            GameController.Window.GetWindowRectangleReal() with { Location = Vector2.Zero }, 250);
        _ingameUiCheckVisible = new TimeCache<bool>(() =>
            IngameUi.FullscreenPanels.Any(x => x.IsVisibleLocal) ||
            IngameUi.LargePanels.Any(x => x.IsVisibleLocal), 250);
        LoadConfig();
        Settings.PlayerZOffset.OnValueChanged += (_, _) => _oldPlayerCoord = Vector2.Zero;
        Settings.PlacePlayerBarRelativeToGroundLevel.OnValueChanged += (_, _) => _oldPlayerCoord = Vector2.Zero;
        Settings.EnableAbsolutePlayerBarPositioning.OnValueChanged += (_, _) => _oldPlayerCoord = Vector2.Zero;
        Settings.ExportDefaultConfig.OnPressed += () => { File.WriteAllText(NewConfigCustomPath, GetEmbeddedConfigString()); };
        return true;
    }

    private void LoadConfig()
    {
        _pathRuleCache.Clear();
        if (Settings.UseOldConfigFormat)
        {
            LoadOldEntityConfigFormat();
        }
        else
        {
            if (File.Exists(NewConfigCustomPath))
            {
                try
                {
                    var content = File.ReadAllText(NewConfigCustomPath);
                    _entityConfig = new IndividualEntityConfig(JsonConvert.DeserializeObject<SerializedIndividualEntityConfig>(content));
                    return;
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError($"Unable to load custom config file, falling back to default: {ex}");
                }
            }

            _entityConfig = LoadEmbeddedConfig();
        }
    }

    private static IndividualEntityConfig LoadEmbeddedConfig()
    {
        var content = GetEmbeddedConfigString();
        return new IndividualEntityConfig(JsonConvert.DeserializeObject<SerializedIndividualEntityConfig>(content));
    }

    private static string GetEmbeddedConfigString()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("entityConfig.default.json");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return content;
    }

    private void LoadOldEntityConfigFormat()
    {
        if (File.Exists(OldConfigPath))
        {
            var ignoredEntities = File.ReadAllLines(OldConfigPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Where(line => !line.StartsWith("#"))
                .ToList();
            _entityConfig = new IndividualEntityConfig(new SerializedIndividualEntityConfig
            {
                EntityPathConfig = ignoredEntities.ToDictionary(
                    x => $"^{Regex.Escape(x)}",
                    _ => new EntityTreatmentRule { Ignore = true }),
            });
        }
        else
        {
            _entityConfig = new IndividualEntityConfig(new SerializedIndividualEntityConfig());
            LogError($"Ignored entities file does not exist. Path: {OldConfigPath}");
        }
    }


    public override void AreaChange(AreaInstance area)
    {
        _oldPlayerCoord = Vector2.Zero;
        LoadConfig();

        // Clear DPS tracking data on area change.
        _damageEvents.Clear();
        _lastHp.Clear();
        _maxDps = 0;
    }

    private bool SkipHealthBar(HealthBar healthBar, bool checkDistance)
    {
        if (healthBar.Settings?.Show != true) return true;
        if (checkDistance && healthBar.Distance > Settings.DrawDistanceLimit) return true;
        if (healthBar.Life == null) return true;
        if (!healthBar.Entity.IsAlive) return true;
        if (healthBar.HpPercent < 0.001f) return true;
        if (healthBar.Type == CreatureType.Minion && healthBar.HpPercent * 100 > Settings.ShowMinionOnlyWhenBelowHp) return true;

        return false;
    }

    private void HpBarWork(HealthBar healthBar)
    {
        healthBar.Skip = SkipHealthBar(healthBar, true);
        if (healthBar.Skip && !ShowInBossOverlay(healthBar)) return;

        healthBar.CheckUpdate();

        var worldCoords = healthBar.Entity.Pos;
        if (!Settings.PlaceBarRelativeToGroundLevel)
        {
            if (healthBar.Entity.GetComponent<Render>()?.Bounds is { } boundsNum)
            {
                worldCoords.Z -= 2 * boundsNum.Z;
            }
        }

        worldCoords.Z += Settings.GlobalZOffset;
        var mobScreenCoords = Camera.WorldToScreen(worldCoords);
        if (mobScreenCoords == Vector2.Zero) return;
        mobScreenCoords = Vector2.Lerp(mobScreenCoords, healthBar.LastPosition, healthBar.LastPosition == Vector2.Zero ? 0 : Math.Clamp(Settings.SmoothingFactor, 0, 1));
        healthBar.LastPosition = mobScreenCoords;
        var scaledWidth = healthBar.Settings.Width * WindowRelativeSize.X;
        var scaledHeight = healthBar.Settings.Height * WindowRelativeSize.Y;

        healthBar.DisplayArea = new ExileCore2.Shared.RectangleF(mobScreenCoords.X - scaledWidth / 2f, mobScreenCoords.Y - scaledHeight / 2f, scaledWidth,
            scaledHeight);

        if (healthBar.Distance > 80 && !_windowRectangle.Value.Intersects(healthBar.DisplayArea))
        {
            healthBar.Skip = true;
        }
    }

    public override void Tick()
    {
        _canTick = true;

        if (!Settings.IgnoreUiElementVisibility && _ingameUiCheckVisible?.Value == true ||
            Camera == null ||
            !Settings.ShowInTown && GameController.Area.CurrentArea.IsTown ||
            !Settings.ShowInHideout && GameController.Area.CurrentArea.IsHideout)
        {
            _canTick = false;
            return;
        }

        TickLogic();
    }

    private void TickLogic()
    {
        foreach (var validEntity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                     .Concat(GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]))
        {
            // After processing all entities, update our global damage tracking.
            UpdateGlobalDamageTracking(validEntity);

            var healthBar = validEntity.GetHudComponent<HealthBar>();
            if (healthBar == null) continue;

            try
            {
                HpBarWork(healthBar);
            }
            catch (Exception e)
            {
                DebugWindow.LogError(e.Message);
            }
        }

        PositionPlayerBar();
    }

    private void PositionPlayerBar()
    {
        if (!Settings.Self.Show || _playerBar is not { } playerBar)
        {
            return;
        }

        var worldCoords = playerBar.Entity.Pos;
        if (!Settings.PlacePlayerBarRelativeToGroundLevel)
        {
            if (playerBar.Entity.GetComponent<Render>()?.Bounds is { } boundsNum)
            {
                worldCoords.Z -= 2 * boundsNum.Z;
            }
        }

        worldCoords.Z += Settings.PlayerZOffset;
        var result = Camera.WorldToScreen(worldCoords);

        if (Settings.EnableAbsolutePlayerBarPositioning)
        {
            _oldPlayerCoord = result = Settings.PlayerBarPosition;
        }
        else
        {
            if (_oldPlayerCoord == Vector2.Zero)
            {
                _oldPlayerCoord = result;
            }
            else if (Settings.PlayerSmoothingFactor >= 1)
            {
                if ((_oldPlayerCoord - result).LengthSquared() < 40 * 40)
                    result = _oldPlayerCoord;
                else
                    _oldPlayerCoord = result;
            }
            else
            {
                result = Vector2.Lerp(result, _oldPlayerCoord, _oldPlayerCoord == Vector2.Zero ? 0 : Math.Max(0, Settings.PlayerSmoothingFactor));
                _oldPlayerCoord = result;
            }
        }

        var scaledWidth = playerBar.Settings.Width * WindowRelativeSize.X;
        var scaledHeight = playerBar.Settings.Height * WindowRelativeSize.Y;

        var background = new ExileCore2.Shared.RectangleF(result.X, result.Y, 0, 0);
        background.Inflate(scaledWidth / 2f, scaledHeight / 2f);
        playerBar.DisplayArea = background;
    }

    /// <summary>
    /// For each monster health bar included in the BossOverlay, check if its HP has dropped
    /// since the last tick. If so, record the difference as damage.
    /// Also prune damage events older than our window.
    /// </summary>
    private void UpdateGlobalDamageTracking(Entity entity)
    {
        var now = DateTime.UtcNow;
        if (entity.GetHudComponent<HealthBar>() is HealthBar healthBar)
        {
            float currentHp = healthBar.Life?.CurHP ?? 0;
            long id = healthBar.Entity.Address;
            if (_lastHp.TryGetValue(id, out float lastHp))
            {
                if (lastHp > currentHp)
                {
                    float damage = lastHp - currentHp;
                    _damageEvents.Add(new DamageEvent { Time = now, Damage = damage });
                }
            }
            _lastHp[id] = currentHp;
        }
        // Remove damage events older than our sliding window.
        _damageEvents.RemoveAll(de => (now - de.Time) > DamageWindow);

        // Compute current DPS (over the sliding window) and update maximum DPS if needed.
        float totalDamage = _damageEvents.Sum(de => de.Damage);
        double currentDps = totalDamage / DamageWindow.TotalSeconds;
        _maxDps = Math.Max(_maxDps, currentDps);
    }

    public override void Render()
    {
        if (!_canTick) return;
        var bossOverlayItems = new List<HealthBar>();
        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                     .Concat(GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]))
        {
            if (entity.GetHudComponent<HealthBar>() is not { } healthBar)
            {
                continue;
            }

            if (!healthBar.Skip)
            {
                try
                {
                    DrawBar(healthBar);
                    if (IsCastBarEnabled(healthBar))
                    {
                        var lifeArea = healthBar.DisplayArea;
                        DrawCastBar(healthBar,
                            lifeArea with
                            {
                                Y = lifeArea.Y + lifeArea.Height * (healthBar.Settings.CastBarSettings.YOffset + 1),
                                Height = healthBar.Settings.CastBarSettings.Height,
                            }, healthBar.Settings.CastBarSettings.ShowStageNames,
                            Settings.CommonCastBarSettings.ShowNextStageName,
                            Settings.CommonCastBarSettings.MaxSkillNameLength);
                    }
                }
                catch (Exception ex)
                {
                    DebugWindow.LogError(ex.ToString());
                }
            }

            if (ShowInBossOverlay(healthBar) && !SkipHealthBar(healthBar, false))
            {
                bossOverlayItems.Add(healthBar);
            }
        }

        bossOverlayItems.Sort((x, y) => x.StableId.CompareTo(y.StableId));
        DrawBossOverlay(bossOverlayItems);

        DrawAverageDps();
    }

    /// <summary>
    /// Calculates the average DPS (total damage divided by the sliding window length)
    /// and draws it in a box to the left of the BossOverlay.
    /// </summary>
    private void DrawAverageDps()
    {
        float totalDamage = _damageEvents.Sum(de => de.Damage);
        double avgDps = totalDamage / DamageWindow.TotalSeconds;
        double maxDps = _maxDps;

        // Define a fixed size and margin for our DPS box.
        const float boxWidth = 130f;
        const float boxHeight = 50f; // increased height for two lines
        const float margin = 8f;
        var bossOverlayLocation = Settings.BossOverlaySettings.Location.Value;
        var dpsBoxRect = new RectangleF(bossOverlayLocation.X - boxWidth - margin, bossOverlayLocation.Y - (boxHeight / 2), boxWidth, boxHeight);

        // Draw a semi–transparent background.
        Graphics.DrawBox(dpsBoxRect.TopLeft, dpsBoxRect.BottomRight, Color.Black.MultiplyAlpha(0.6f));

        // Build display texts.
        string avgText = $"Avg DPS: {avgDps.FormatHp()}";
        string maxText = $"Max DPS: {maxDps.FormatHp()}";

        var avgTextSize = Graphics.MeasureText(avgText);
        var maxTextSize = Graphics.MeasureText(maxText);

        // Position the average DPS text in the top half, and the max DPS text in the bottom half.
        var avgTextPos = new Vector2(
            dpsBoxRect.X + (boxWidth - avgTextSize.X) / 2,
            dpsBoxRect.Y + (boxHeight / 2 - avgTextSize.Y) / 2);
        var maxTextPos = new Vector2(
            dpsBoxRect.X + (boxWidth - maxTextSize.X) / 2,
            dpsBoxRect.Y + (boxHeight / 2) + ((boxHeight / 2 - maxTextSize.Y) / 2));

        Graphics.DrawText(avgText, avgTextPos, Color.White);
        Graphics.DrawText(maxText, maxTextPos, Color.White);
    }

    private void DrawBossOverlay(IEnumerable<HealthBar> items)
    {
        if (!Settings.BossOverlaySettings.Show)
        {
            return;
        }

        var barPosition = Settings.BossOverlaySettings.Location.Value;
        foreach (var healthBar in items.Take(Settings.BossOverlaySettings.MaxEntries))
        {
            try
            {
                var lifeRect = new RectangleF(barPosition.X, barPosition.Y, Settings.BossOverlaySettings.Width, Settings.BossOverlaySettings.BarHeight);
                DrawBar(healthBar, lifeRect, false, true, Settings.BossOverlaySettings.ShowMonsterNames ? healthBar.Entity.RenderName : null, true);
                barPosition.Y += lifeRect.Height + 3;

                if (IsCastBarEnabled(healthBar))
                {
                    if (DrawCastBar(healthBar, lifeRect with { Y = barPosition.Y },
                        Settings.BossOverlaySettings.ShowCastBarStageNames,
                        Settings.CommonCastBarSettings.ShowNextStageNameInBossOverlay,
                        Settings.CommonCastBarSettings.MaxSkillNameLengthForBossOverlay))
                    {
                        barPosition.Y += lifeRect.Height + 3;
                    }
                }

                var buffRect = new RectangleF(barPosition.X - 1, barPosition.Y, lifeRect.Width, 0);
                var buffsHeight = DrawBuffs(healthBar, buffRect);
                barPosition.Y += buffsHeight;

            }
            catch (Exception ex)
            {
                DebugWindow.LogError(ex.ToString());
            }

            barPosition.Y += Settings.BossOverlaySettings.ItemSpacing;
        }
    }

    private void DrawBar(HealthBar bar)
    {
        var enableResizing = Settings.ResizeBarsToFitText;
        var showDps = bar.Settings.ShowDps;
        DrawBar(bar, bar.DisplayArea, enableResizing, showDps, null);
    }

    private void DrawBar(HealthBar bar, RectangleF barArea, bool enableResizing, bool showDps, string textPrefix, bool bossOverlay = false)
    {
        var barText = $"{textPrefix} {GetTemplatedText(bar)}";
        barText = string.IsNullOrWhiteSpace(barText) ? null : barText.Trim();

        if (barText != null && enableResizing)
        {
            var barTextSize = Graphics.MeasureText(barText);
            barArea.Inflate(Math.Max(0, (barTextSize.X - barArea.Width) / 2), Math.Max(0, (barTextSize.Y - barArea.Height) / 2));
        }

        var alphaMulti = GetAlphaMulti(bar, barArea);
        if (alphaMulti == 0)
        {
            return;
        }

        Graphics.DrawImage(HealthbarTexture, barArea, bar.Settings.BackgroundColor.MultiplyAlpha(alphaMulti));
        var barSources = new List<(float Current, float Max, Color Color)>();
        barSources.Add((bar.Life.CurHP, bar.Life.MaxHP, bar.Color));
        if (bar.Settings.CombineLifeAndEs)
        {
            barSources.Add((bar.Life.CurES, bar.Life.MaxES, bar.Settings.EsColor));
        }

        if (bar.Settings.CombineLifeAndMana)
        {
            barSources.Add((bar.Life.CurMana, bar.Life.MaxMana, bar.Settings.ManaColor));
        }

        var totalPool = barSources.Sum(x => x.Max);
        var currentLeft = barArea.Left;
        foreach (var barSource in barSources)
        {
            if (barSource.Current > 0)
            {
                var width = barArea.Width * barSource.Current / totalPool;
                Graphics.DrawImage(HealthbarTexture, barArea with { Left = currentLeft, Width = width }, barSource.Color.MultiplyAlpha(alphaMulti));
                currentLeft += width;
            }
        }

        if (!bar.Settings.CombineLifeAndEs)
        {
            var esWidth = barArea.Width * bar.EsPercent;
            Graphics.DrawImage(HealthbarTexture, new RectangleF(barArea.X, barArea.Y, esWidth, barArea.Height * bar.Settings.EsBarHeight),
                bar.Settings.EsColor.MultiplyAlpha(alphaMulti));
        }

        var segmentCount = bar.Settings.HealthSegments.Value;
        for (int i = 1; i < segmentCount; i++)
        {
            var x = i / (float)segmentCount * barArea.Width;
            var notchRect = new RectangleF(
                barArea.X + x,
                barArea.Bottom - barArea.Height * bar.Settings.HealthSegmentHeight,
                1,
                barArea.Height * bar.Settings.HealthSegmentHeight);
            Graphics.DrawImage(FlatHealthbarTexture, notchRect, bar.Settings.HealthSegmentColor.MultiplyAlpha(alphaMulti));
        }

        if (bar.Settings.OutlineThickness > 0 && bar.Settings.OutlineColor.Value.A > 0)
        {
            var outlineRect = barArea;
            outlineRect.Inflate(1, 1);
            Graphics.DrawFrame(outlineRect, bar.Settings.OutlineColor.MultiplyAlpha(alphaMulti), bar.Settings.OutlineThickness.Value);
        }

        ShowHealthbarText(bar, barText, alphaMulti, barArea);
        if (showDps)
        {
            ShowDps(bar, alphaMulti, barArea, bossOverlay);
        }
    }

    private Color GetContrastingTextColor(Color background)
    {
        // Use the built-in GetBrightness method.
        float brightness = background.GetBrightness();
        return brightness > 0.5f ? Color.Black : Color.White;
    }

    private static float GetAlphaMulti(HealthBar bar, RectangleF barArea)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        var alphaMulti = bar.Settings.HoverOpacity != 1
                         && ImGui.IsMouseHoveringRect(barArea.TopLeft, barArea.BottomRight, false)
            ? bar.Settings.HoverOpacity
            : 1f;
        return alphaMulti;
    }

    private void ShowDps(HealthBar bar, float alphaMulti, RectangleF area, bool BossOverlay = false)
    {
        const int margin = 2;
        if (bar.EhpHistory.Count < 2) return;
        var hpFirst = bar.EhpHistory.First();
        var hpLast = bar.EhpHistory.Last();

        var timeDiff = hpLast.Time - hpFirst.Time;
        var hpDiff = hpFirst.Value - hpLast.Value;

        var dps = hpDiff / timeDiff.TotalSeconds;
        if (dps == 0)
        {
            return;
        }

        var damageColor = dps < 0
            ? Settings.CombatHealColor
            : Settings.CombatDamageColor;

        var dpsText = "DPS: " + dps.FormatHp();
        var textArea = Graphics.MeasureText(dpsText);
        Vector2 textCenter;
        if (BossOverlay)
        {
            textCenter = new Vector2(area.Right - textArea.X / 2, area.Top - textArea.Y / 2 - margin);
        }
        else
        {
            textCenter = new Vector2(area.Center.X, area.Bottom + textArea.Y / 2 + margin);
        }

        Graphics.DrawBox(textCenter - textArea / 2, textCenter + textArea / 2, bar.Settings.TextBackground.MultiplyAlpha(alphaMulti));
        Graphics.DrawText(dpsText, textCenter - textArea / 2, damageColor.MultiplyAlpha(alphaMulti));
    }

    private static readonly Dictionary<string, Color> BuffBackgroundColorLookup = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        { "Drought", Color.FromArgb(130, 68, 13) },
        { "Intangibility", Color.FromArgb(130, 40, 13) },
        { "Stunned", Color.FromArgb(170, 163, 50) },
        { "Frozen", Color.FromArgb(18, 151, 180) },
        { "Chilled", Color.FromArgb(70, 152, 170) },
        { "Lightning Clone Retaliation", Color.FromArgb(14, 87, 180) },
        { "Shocked", Color.FromArgb(21, 116, 188) },
        { "Speed Aura", Color.FromArgb(0, 255, 84) },
        { "Executioner's Presence", Color.FromArgb(128, 128, 128) },
        { "Blinded", Color.FromArgb(128, 128, 128) },
        { "Poisoned", Color.FromArgb(14, 134, 6) },
        { "Resists Aura", Color.FromArgb(146, 149, 19) },
        { "Ignited", Color.FromArgb(118, 3, 3) },
        { "Withered", Color.FromArgb(89, 6, 162) },
        { "Tempest Shrine", Color.FromArgb(11, 118, 165) },
        { "Energy Shield Aura", Color.FromArgb(95, 131, 14) },
        { "Armour Break", Color.FromArgb(128, 128, 128) },
        { "Bleeding", Color.FromArgb(138, 22, 22) },
        { "Freezing Shrine", Color.FromArgb(50, 67, 218) },
        { "Meteoric Shrine", Color.FromArgb(163, 75, 36) },
        { "Maimed", Color.FromArgb(140, 17, 17) },
        { "Critical Weakness", Color.FromArgb(237, 236, 16) },
        { "Thaumaturgist's Mark", Color.FromArgb(128, 128, 128) },
        { "Fire Exposure", Color.FromArgb(126, 7, 7) },
        { "Consecrated Ground", Color.FromArgb(99, 68, 21) },
        { "Burning", Color.FromArgb(140, 58, 20) },
        { "Frenzied", Color.FromArgb(109, 15, 15) },
        { "Infernal Cry", Color.FromArgb(125, 18, 108) },
        { "Physical Damage Aura", Color.FromArgb(15, 70, 112) },
        { "Temporal Bubble", Color.FromArgb(84, 0, 165) },
        { "Lightning Exposure", Color.FromArgb(0, 88, 179) },
        { "Intervention", Color.FromArgb(128, 128, 128) },
        { "Pinned", Color.FromArgb(8, 128, 67) },
        { "Jagged Ground", Color.FromArgb(67, 43, 3) },
        { "Pride", Color.FromArgb(128, 128, 128) },
        { "Frost Bomb", Color.FromArgb(12, 4, 138) },
        { "Cold Exposure", Color.FromArgb(66, 143, 208) },
        { "Faster Run", Color.FromArgb(128, 128, 128) },
        { "Living Blood", Color.FromArgb(110, 37, 7) },
        { "Dazed", Color.FromArgb(112, 34, 151) },
        { "Siphoning Ring", Color.FromArgb(128, 128, 128) },
        { "Acceleration Shrine", Color.FromArgb(128, 128, 128) },
        { "Hinder", Color.FromArgb(112, 34, 151) },
        { "Phasing Buff", Color.FromArgb(112, 34, 151) },
}   ;

    /// <summary>
    /// Draws buffs underneath a given area. Multiple buffs with the same DisplayName are merged
    /// into one entry that shows the stack count (if > 1) before the timer.
    /// Buffs are sorted alphabetically and drawn in–line with wrapping.
    /// </summary>
    /// <param name="bar">The HealthBar containing the entity and its buffs.</param>
    /// <param name="area">The drawing area (X and Width are used; Y is the top baseline).</param>
    /// <returns>The total vertical height used to draw the buffs.</returns>
    private float DrawBuffs(HealthBar bar, RectangleF area)
    {
        // Define margins and padding.
        const float horizontalMargin = 4f; // space between buff boxes
        const float verticalMargin = 2f;   // space between lines
        const float padding = 2f;          // padding inside each buff's box

        float totalHeight = 0f;            // vertical offset for drawn lines
        float currentLineHeight = 0f;      // maximum height of the current line
        float currentX = area.X;           // current X position where the next buff will be drawn

        var alphaMulti = GetAlphaMulti(bar, area);

        // Get buffs with a non-empty DisplayName.
        var buffs = bar.Entity.Buffs?
            .Where(buff => !string.IsNullOrEmpty(buff.DisplayName))
            .ToList();

        if (buffs == null || buffs.Count == 0)
            return 0f;

        // Group buffs by DisplayName.
        // For each group, count the number of occurrences and pick the maximum Timer value.
        var groupedBuffs = buffs
            .GroupBy(b => b.DisplayName)
            .Select(g => new
            {
                DisplayName = g.Key,
                Count = g.Count(),
                Timer = g.Max(b => b.Timer)
            })
            .OrderBy(x => x.DisplayName)
            .ToList();

        // The available width is the width of the provided area.
        float availableWidth = area.Width;

        // Loop through each unique buff.
        foreach (var uniqueBuff in groupedBuffs)
        {
            // Build the display text:
            // Start with the DisplayName.
            string displayText = uniqueBuff.DisplayName;
            // If more than one instance exists, append the stack count.
            if (uniqueBuff.Count > 1)
            {
                displayText += " (" + uniqueBuff.Count.ToString() + ")";
            }
            // Append the timer if applicable.
            if (uniqueBuff.Timer > 0 && uniqueBuff.Timer < 99)
            {
                displayText += $" {uniqueBuff.Timer:F1}s";
            }

            // Determine the background color via the lookup table.
            // (Default to the plugin’s TextBackground if no keyword matches.)
            Color bgColor = Color.DarkSlateGray;
            foreach (var kvp in BuffBackgroundColorLookup)
            {
                if (displayText.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bgColor = kvp.Value;
                    break;
                }
            }

            // Choose a contrasting text color (either white or black) based on the background brightness.
            Color textColor = GetContrastingTextColor(bgColor);

            // Measure the complete display text.
            var textSize = Graphics.MeasureText(displayText);
            float boxWidth = textSize.X + 2 * padding;
            float boxHeight = textSize.Y + 2 * padding;

            // If adding this buff would exceed the available width, wrap to the next line.
            if (currentX - area.X + boxWidth > availableWidth)
            {
                totalHeight += currentLineHeight + verticalMargin;
                currentX = area.X;
                currentLineHeight = 0f;
            }

            // Define the box rectangle for this buff cell.
            var boxRect = new RectangleF(currentX, area.Y + totalHeight, boxWidth, boxHeight);
            Graphics.DrawBox(boxRect.TopLeft, boxRect.BottomRight, bgColor.MultiplyAlpha(alphaMulti));

            // Draw the text inside the box.
            var textPos = new Vector2(currentX + padding, area.Y + totalHeight + padding);
            Graphics.DrawText(displayText, textPos, textColor.MultiplyAlpha(alphaMulti));

            // Move currentX to the next position.
            currentX += boxWidth + horizontalMargin;
            // Update the current line height if necessary.
            currentLineHeight = Math.Max(currentLineHeight, boxHeight);
        }

        // Add the height of the last line.
        totalHeight += currentLineHeight;

        return totalHeight;
    }


    private void ShowHealthbarText(HealthBar bar, string text, float alphaMulti, RectangleF area)
    {
        if (text != null)
        {
            var textArea = Graphics.MeasureText(text);
            var barCenter = area.Center;
            var textOffset = bar.Settings.TextPosition.Value.Mult(area.Width + textArea.X, area.Height + textArea.Y) / 2;
            var textCenter = barCenter + textOffset;
            var textTopLeft = textCenter - textArea / 2;
            var textRect = new RectangleF(textTopLeft.X, textTopLeft.Y, textArea.X, textArea.Y);
            area.Contains(ref textRect, out var textIsInsideBar);
            if (!textIsInsideBar)
            {
                Graphics.DrawBox(textTopLeft, textTopLeft + textArea, bar.Settings.TextBackground.MultiplyAlpha(alphaMulti));
            }

            Graphics.DrawText(text, textTopLeft, bar.Settings.TextColor.MultiplyAlpha(alphaMulti));
        }
    }

    private static string GetTemplatedText(HealthBar bar)
    {
        var textFormat = bar.Settings.TextFormat.Value;
        if (string.IsNullOrWhiteSpace(textFormat))
        {
            return null;
        }

        return textFormat
            .Replace("{percent}", Math.Floor(bar.EhpPercent * 100).ToString(CultureInfo.InvariantCulture))
            .Replace("{current}", bar.CurrentEhp.FormatHp())
            .Replace("{total}", bar.MaxEhp.FormatHp())
            .Replace("{currentes}", bar.Life.CurES.FormatHp())
            .Replace("{currentlife}", bar.Life.CurHP.FormatHp());
    }

    private static readonly HashSet<string> DangerousStages = new HashSet<string>
    {
        "contact",
        "slam",
        "teleport",
        "small_beam_blast",
        "medium_beam_blast",
        "large_beam_blast",
        "clone_beam_blast",
        "beam_l",
        "beam_r",
        "clap",
        "stab",
        "slash",
        "ice_shard",
        "wind_force",
        "wave",
    };

    private static readonly string TexturePrefix = "hb_";

    private bool DrawCastBar(HealthBar bar, RectangleF area, bool drawStageNames, bool showNextStageName, int maxSkillNameLength)
    {
        bool retValue = false;
        if (!bar.Entity.TryGetComponent<Actor>(out var actor))
        {
            return false;
        }

        if (actor?.AnimationController is not { } ac || actor.Action != ActionFlags.UsingAbility || ac.RawAnimationSpeed == 0)
        {
            return false;
        }

        var stages = ac.CurrentAnimation.AllStages.ToList();
        var settings = bar.Settings.CastBarSettings;
        var maxRawProgress = Settings.CommonCastBarSettings.CutOffBackswing
            ? stages.LastOrDefault(x => DangerousStages.Contains(x.StageNameSafe()))?.StageStart ?? ac.MaxRawAnimationProgress
            : ac.MaxRawAnimationProgress;
        if (ac.RawAnimationProgress > maxRawProgress)
        {
            return false;
        }

        var alphaMulti = GetAlphaMulti(bar, area);
        if (alphaMulti == 0)
        {
            return false;
        }

        var width = area.Width;
        var height = area.Height;
        var maxProgress = ac.TransformProgress(maxRawProgress);
        var topLeft = area.TopLeft;
        var bottomRight = topLeft + new Vector2(width, height);
        Graphics.DrawBox(topLeft, bottomRight, settings.BackgroundColor.MultiplyAlpha(alphaMulti));
        Graphics.DrawBox(topLeft, topLeft + new Vector2(width * ac.TransformedRawAnimationProgress / maxProgress, height), settings.FillColor.MultiplyAlpha(alphaMulti));

        var nextDangerousStage = stages.FirstOrDefault(x => x.StageStart > ac.RawAnimationProgress && DangerousStages.Contains(x.StageNameSafe()));
        var stageIn = nextDangerousStage != null
            ? (ac.TransformProgress(nextDangerousStage.StageStart) - ac.TransformedRawAnimationProgress) / ac.AnimationSpeed
            : ac.AnimationCompletesIn.TotalSeconds;
        var mainText = (nextDangerousStage != null && showNextStageName, maxSkillNameLength) switch
        {
            (true, <= 0) => $"{nextDangerousStage?.StageNameSafe()} in {stageIn:F1}",
            (false, <= 0) => $"{stageIn:F1}",
            (true, var v and > 0) => $"{actor.CurrentAction?.Skill?.Name?.Truncate(v)} {nextDangerousStage?.StageNameSafe()} in {stageIn:F1}",
            (false, var v and > 0) => $"{actor.CurrentAction?.Skill?.Name?.Truncate(v)} in {stageIn:F1}",
        };
        var oldTextSize = Graphics.MeasureText(mainText);
        using (Graphics.SetTextScale(Math.Min(height / oldTextSize.Y, width / oldTextSize.X)))
        {
            var color = (nextDangerousStage != null ? settings.DangerTextColor : settings.NoDangerTextColor).MultiplyAlpha(alphaMulti);
            Graphics.DrawText(mainText, topLeft, color);
            retValue = true;
        }

        var occupiedSlots = new Dictionary<int, float>();
        var textLineHeight = Graphics.MeasureText("A").Y;
        var displayAllSkillStages = Settings.CommonCastBarSettings.DebugShowAllSkillStages;
        foreach (var stage in stages.Where(x => displayAllSkillStages || DangerousStages.Contains(x.StageNameSafe())))
        {
            var normalizedStageStart = ac.TransformProgress(stage.StageStart) / maxProgress;
            if (ReferenceEquals(stage, nextDangerousStage) && Math.Abs(normalizedStageStart - 1) < 1e-3)
            {
                continue;
            }

            var stageX = topLeft.X + normalizedStageStart * width;
            if (drawStageNames)
            {
                var line = Enumerable.Range(0, 100).FirstOrDefault(x => occupiedSlots.GetValueOrDefault(x, float.NegativeInfinity) < stageX);
                var text = displayAllSkillStages ? $"{normalizedStageStart}:{stage.StageNameSafe()}" : $"{stage.StageNameSafe()}";
                var textSize = Graphics.MeasureText(text);
                occupiedSlots[line] = stageX + textSize.X + 20;
                var textStart = new Vector2(stageX, topLeft.Y + height + line * textLineHeight);
                Graphics.DrawBox(textStart, textStart + textSize, settings.BackgroundColor.MultiplyAlpha(alphaMulti));
                Graphics.DrawText(text, textStart, settings.StageTextColor.MultiplyAlpha(alphaMulti));
                Graphics.DrawLine(textStart, topLeft with { X = textStart.X }, 1, Color.Green.MultiplyAlpha(alphaMulti));
                retValue = true;
            }
            else
            {
                Graphics.DrawLine(topLeft with { X = stageX }, bottomRight with { X = stageX }, 1, Color.Green.MultiplyAlpha(alphaMulti));
                retValue = true;
            }
        }

        return true;
    }

    public override void EntityAdded(Entity entity)
    {
        if (entity.Type != EntityType.Monster && entity.Type != EntityType.Player ||
            entity.GetComponent<Life>() != null && !entity.IsAlive ||
            FindRule(entity.Path).Ignore == true)
        {
            return;
        }

        var healthBar = new HealthBar(entity, Settings);
        entity.SetHudComponent(healthBar);
        if (entity.Address == GameController.Player.Address)
        {
            _playerBar = healthBar;
        }
    }

    private EntityTreatmentRule FindRule(string path)
    {
        return _pathRuleCache.GetOrAdd(path, p => _entityConfig.Rules.FirstOrDefault(x => x.Regex.IsMatch(p)).Rule ?? new EntityTreatmentRule());
    }

    private bool ShowInBossOverlay(HealthBar bar)
    {
        return Settings.BossOverlaySettings.Show &&
               (FindRule(bar.Entity.Path).ShowInBossOverlay ?? bar.Settings.IncludeInBossOverlay.Value);
    }

    private bool IsCastBarEnabled(HealthBar bar)
    {
        return FindRule(bar.Entity.Path).ShowCastBar ?? bar.Settings.CastBarSettings.Show.Value;
    }
}