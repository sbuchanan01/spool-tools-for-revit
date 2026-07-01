using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Per-project preferences for the Spool tool. Persisted on ProjectInfo via ExtensibleStorage
    /// so each user gets back their last-used titleblock, schedule, and view selection.
    /// </summary>
    public class SpoolSettings
    {
        public long? TitleblockTypeId { get; set; }
        public long? ScheduleId       { get; set; }
        public int   DirectionMask    { get; set; } = DefaultMask;
        /// <summary>Last-chosen view-scale denominator (12, 48, 96, …). Null = Auto Fit.</summary>
        public int?  ScaleDenominator { get; set; }
        /// <summary>Last-chosen FabricationPipework tag FamilySymbol id.
        /// Null = "Do not place Tags".</summary>
        public long? TagFamilyId      { get; set; }
        /// <summary>Last-chosen view template id. Null = "(No template)".</summary>
        public long? ViewTemplateId   { get; set; }
        /// <summary>Last-chosen interactive-tagging flag.</summary>
        public bool  InteractiveTagging { get; set; }

        /// <summary>Last-chosen "place leader" flag from the Leader Settings popup.</summary>
        public bool   PlaceLeader        { get; set; }
        /// <summary>Last-chosen leader end style. 0 = Attached (default), 1 = Free.</summary>
        public int    LeaderEnd          { get; set; } = 0;
        /// <summary>Last-chosen leader length in feet (0 = no elbow offset).</summary>
        public double LeaderLengthFt     { get; set; } = 0.0;

        /// <summary>Distance (PAPER inches) between part and tag head.
        /// Default 1.0 = historical hardcoded offset. Distinct from
        /// LeaderLengthFt (shoulder).</summary>
        public double TagOffsetInches    { get; set; } = 1.0;

        // ── Renumber preferences (remembered between tool invocations) ────────

        public bool RenumberEnabled              { get; set; }
        public int  RenumberStartingNumber       { get; set; } = 1;
        public bool RenumberUseSameForIdentical  { get; set; } = true;
        public bool RenumberUseLengthAsSeparator { get; set; }
        /// <summary>"Include Welds" toggle (default true). When false the
        /// renumber + tag steps skip parts whose Product Range is "Joints".</summary>
        public bool IncludeWelds                 { get; set; } = true;

        /// <summary>"Use Assemblies" toggle (default false). When on, each
        /// spool becomes a Revit AssemblyInstance (members + assembly
        /// views + sheet) instead of an ad-hoc 3D view tree on a normal
        /// sheet. Shared between Create Spool and The Spooler.</summary>
        public bool UseAssemblies                { get; set; }

        // ── The Spooler (batch tool) preferences ──────────────────────────────

        /// <summary>Identifier substituted for the {ID} token in The Spooler.</summary>
        public string SpoolerIdentifier          { get; set; } = "001";
        /// <summary>Spool number template (e.g. <c>{Service}-{ID}-{N:00}</c>).
        /// Default padding is 2 digits (<c>01</c>, <c>02</c>, …, <c>99</c>) —
        /// matches the typical per-batch spool count for a single area /
        /// level. Users with larger batches can extend to <c>{N:000}</c> in
        /// the Spool Config dialog.</summary>
        public string SpoolerNumberTemplate      { get; set; } = "{Service}-{ID}-{N:00}";
        /// <summary>Spool name template (e.g. <c>Spool {Number}</c>).</summary>
        public string SpoolerNameTemplate        { get; set; } = "Spool {Number}";
        /// <summary>Starting value for the {N} counter.</summary>
        public int    SpoolerStartingSequence    { get; set; } = 1;
        /// <summary>Starting sheet number (e.g. <c>S1</c>). Trailing digits
        /// increment per spool; padding is inferred from leading zeros.</summary>
        public string SpoolerStartingSheetNumber { get; set; } = "S1";

        // ── The Spooler — Auto-Split Rules ────────────────────────────────────

        public bool   SpoolerRuleAtFieldWelds     { get; set; }
        public bool   SpoolerRuleMaxWeightEnabled { get; set; }
        public string SpoolerRuleMaxWeightLbText  { get; set; } = "1000";
        public bool   SpoolerRuleMaxLengthEnabled { get; set; }
        public string SpoolerRuleMaxLengthText    { get; set; } = "20";

        /// <summary>"Convert isolated welds to Field Welds" toggle in
        /// The Spooler. Default off. Controls whether the batch tool
        /// stamps "Field Weld" on the Comments parameter of welds that
        /// would otherwise be the sole part of a spool. The fold-into-
        /// next-spool behaviour itself is always on (a lone weld never
        /// gets its own spool drawing) — this flag only governs the
        /// relabel.</summary>
        public bool   SpoolerConvertSplitWeldsToFieldWelds { get; set; }

        /// <summary>Name of the project text-type parameter that
        /// SpoolService writes to as the "spool status" step (e.g.
        /// "Fabrication Status"). Empty = skip the status write
        /// entirely. Defaults preserve the historical hard-coded
        /// behaviour: <see cref="SpoolNumberRegistry.FabricationStatusParam"/>
        /// + <see cref="SpoolNumberRegistry.FabricationStatusValue"/>.</summary>
        public string SpoolStatusParamName  { get; set; } = SpoolNumberRegistry.FabricationStatusParam;
        public string SpoolStatusParamValue { get; set; } = SpoolNumberRegistry.FabricationStatusValue;

        // ── Dimensions ────────────────────────────────────────────────────────

        /// <summary>Project DimensionType element id to apply to spool
        /// dimensions. Null = use Revit's default linear style.</summary>
        public long? SpoolDimensionStyleId         { get; set; }

        /// <summary>"Include Dimensions" default for the Create Spool /
        /// The Spooler dialogs. Per-run dialog still has its own
        /// checkbox that can override.</summary>
        public bool   SpoolIncludeDimensionsDefault { get; set; }

        /// <summary>Master switch for the bbox-avoidance + part-shape
        /// aware tag placement. OFF = historical 1" up behaviour.</summary>
        public bool   EnhancedTagPlacement          { get; set; }

        /// <summary>Distance (in inches) between the dimensioned
        /// element and the dim line. Used as the base offset; the
        /// SpoolDimensioner stacks additional dim layers at multiples
        /// of this value so the inner / segment / overall chains don't
        /// collide.</summary>
        public double SpoolDimensionOffsetInches   { get; set; } = 6.0;

        public const int DefaultMask =
            (1 << (int)SpoolDirection.Top)   |
            (1 << (int)SpoolDirection.Front) |
            (1 << (int)SpoolDirection.NeIso);

        public static int Encode(IEnumerable<SpoolDirection> dirs)
        {
            int m = 0;
            foreach (var d in dirs) m |= (1 << (int)d);
            return m;
        }

        public static IReadOnlyList<SpoolDirection> Decode(int mask)
        {
            var list = new List<SpoolDirection>();
            foreach (SpoolDirection d in Enum.GetValues(typeof(SpoolDirection)))
                if ((mask & (1 << (int)d)) != 0) list.Add(d);
            return list;
        }

        // ── ExtensibleStorage persistence ──────────────────────────────────────

        private static readonly Guid SchemaGuid = new Guid("A68289B3-CDA6-4C29-A7C1-973B9A0D93DE");
        private const string SchemaName = "SpoolTools_SpoolSettings";
        private const string FieldData  = "Data";

        public static SpoolSettings Load(Document doc)
        {
            var info = new FilteredElementCollector(doc).OfClass(typeof(ProjectInfo)).FirstOrDefault();
            if (info == null) return new SpoolSettings();

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new SpoolSettings();

            var ent = info.GetEntity(schema);
            if (ent == null || !ent.IsValid()) return new SpoolSettings();

            return Deserialize(ent.Get<string>(FieldData) ?? string.Empty);
        }

        public static void Save(Document doc, SpoolSettings s)
        {
            var info = new FilteredElementCollector(doc).OfClass(typeof(ProjectInfo)).FirstOrDefault();
            if (info == null) return;

            var schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
            var ent = new Entity(schema);
            ent.Set(FieldData, Serialize(s));
            info.SetEntity(ent);
        }

        private static Schema CreateSchema()
        {
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(FieldData, typeof(string));
            return b.Finish();
        }

        // Minimal flat key:value:key:value text encoding to avoid pulling in a JSON dep.
        // New keys are append-only — older payloads stay readable.
        // Free-text values are URI-escaped so semicolons / equals signs the
        // user types into a template don't corrupt the on-disk format.
        private static string Serialize(SpoolSettings s) =>
            $"tb={s.TitleblockTypeId?.ToString() ?? ""};" +
            $"sch={s.ScheduleId?.ToString() ?? ""};" +
            $"dir={s.DirectionMask};" +
            $"scl={s.ScaleDenominator?.ToString() ?? ""};" +
            $"tag={s.TagFamilyId?.ToString() ?? ""};" +
            $"vt={s.ViewTemplateId?.ToString() ?? ""};" +
            $"itag={(s.InteractiveTagging ? 1 : 0)};" +
            $"ld_on={(s.PlaceLeader ? 1 : 0)};" +
            $"ld_end={s.LeaderEnd};" +
            $"ld_len={s.LeaderLengthFt.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
            $"tag_off={s.TagOffsetInches.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
            $"rn_on={(s.RenumberEnabled ? 1 : 0)};" +
            $"rn_st={s.RenumberStartingNumber};" +
            $"rn_id={(s.RenumberUseSameForIdentical ? 1 : 0)};" +
            $"rn_ln={(s.RenumberUseLengthAsSeparator ? 1 : 0)};" +
            $"inc_w={(s.IncludeWelds ? 1 : 0)};" +
            $"use_asm={(s.UseAssemblies ? 1 : 0)};" +
            $"sp_id={Esc(s.SpoolerIdentifier)};" +
            $"sp_nt={Esc(s.SpoolerNumberTemplate)};" +
            $"sp_nm={Esc(s.SpoolerNameTemplate)};" +
            $"sp_seq={s.SpoolerStartingSequence};" +
            $"sp_shn={Esc(s.SpoolerStartingSheetNumber)};" +
            $"sp_rfw={(s.SpoolerRuleAtFieldWelds ? 1 : 0)};" +
            $"sp_rwe={(s.SpoolerRuleMaxWeightEnabled ? 1 : 0)};" +
            $"sp_rwt={Esc(s.SpoolerRuleMaxWeightLbText)};" +
            $"sp_rle={(s.SpoolerRuleMaxLengthEnabled ? 1 : 0)};" +
            $"sp_rlt={Esc(s.SpoolerRuleMaxLengthText)};" +
            $"sp_cwfw={(s.SpoolerConvertSplitWeldsToFieldWelds ? 1 : 0)};" +
            $"st_pn={Esc(s.SpoolStatusParamName)};" +
            $"st_pv={Esc(s.SpoolStatusParamValue)};" +
            $"dim_st={s.SpoolDimensionStyleId?.ToString() ?? ""};" +
            $"dim_inc={(s.SpoolIncludeDimensionsDefault ? 1 : 0)};" +
            $"dim_off={s.SpoolDimensionOffsetInches.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
            $"etp={(s.EnhancedTagPlacement ? 1 : 0)}";

        private static string Esc(string? v) =>
            string.IsNullOrEmpty(v) ? "" : System.Uri.EscapeDataString(v);
        private static string Unesc(string v) =>
            string.IsNullOrEmpty(v) ? "" : System.Uri.UnescapeDataString(v);

        private static SpoolSettings Deserialize(string text)
        {
            var s = new SpoolSettings();
            if (string.IsNullOrWhiteSpace(text)) return s;

            foreach (var part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part[..eq];
                string val = part[(eq + 1)..];

                switch (key)
                {
                    case "tb":     s.TitleblockTypeId = long.TryParse(val, out var t) ? t : null; break;
                    case "sch":    s.ScheduleId       = long.TryParse(val, out var c) ? c : null; break;
                    case "dir":    s.DirectionMask    = int.TryParse(val, out var d) ? d : DefaultMask; break;
                    case "scl":    s.ScaleDenominator = int.TryParse(val, out var sc) ? sc : null; break;
                    case "tag":    s.TagFamilyId      = long.TryParse(val, out var tg) ? tg : null; break;
                    case "vt":     s.ViewTemplateId   = long.TryParse(val, out var vt) ? vt : null; break;
                    case "itag":   s.InteractiveTagging = val == "1"; break;
                    case "ld_on":  s.PlaceLeader        = val == "1"; break;
                    case "ld_end": s.LeaderEnd          = int.TryParse(val, out var le) ? le : 0; break;
                    case "ld_len": s.LeaderLengthFt     = double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ll) ? ll : 0.0; break;
                    case "tag_off": s.TagOffsetInches   = double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var toff) && toff > 0 ? toff : 1.0; break;
                    case "inc_w":  s.IncludeWelds                 = val == "1"; break;
                    case "use_asm": s.UseAssemblies               = val == "1"; break;
                    case "rn_on":  s.RenumberEnabled              = val == "1"; break;
                    case "rn_st":  s.RenumberStartingNumber       = int.TryParse(val, out var rs) ? rs : 1; break;
                    case "rn_id":  s.RenumberUseSameForIdentical  = val == "1"; break;
                    case "rn_ln":  s.RenumberUseLengthAsSeparator = val == "1"; break;
                    case "sp_id":  s.SpoolerIdentifier          = Unesc(val); break;
                    case "sp_nt":  s.SpoolerNumberTemplate      = Unesc(val); break;
                    case "sp_nm":  s.SpoolerNameTemplate        = Unesc(val); break;
                    case "sp_seq": s.SpoolerStartingSequence    = int.TryParse(val, out var ssq) ? ssq : 1; break;
                    case "sp_shn": s.SpoolerStartingSheetNumber = Unesc(val); break;
                    case "sp_rfw": s.SpoolerRuleAtFieldWelds     = val == "1"; break;
                    case "sp_rwe": s.SpoolerRuleMaxWeightEnabled = val == "1"; break;
                    case "sp_rwt": s.SpoolerRuleMaxWeightLbText  = Unesc(val); break;
                    case "sp_rle": s.SpoolerRuleMaxLengthEnabled = val == "1"; break;
                    case "sp_rlt": s.SpoolerRuleMaxLengthText    = Unesc(val); break;
                    case "sp_cwfw": s.SpoolerConvertSplitWeldsToFieldWelds = val == "1"; break;
                    case "st_pn":   s.SpoolStatusParamName  = Unesc(val); break;
                    case "st_pv":   s.SpoolStatusParamValue = Unesc(val); break;
                    case "dim_st":  s.SpoolDimensionStyleId = long.TryParse(val, out var dst) ? dst : null; break;
                    case "dim_inc": s.SpoolIncludeDimensionsDefault = val == "1"; break;
                    case "dim_off": s.SpoolDimensionOffsetInches = double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doff) ? doff : 6.0; break;
                    case "etp":     s.EnhancedTagPlacement = val == "1"; break;
                }
            }
            return s;
        }
    }
}
