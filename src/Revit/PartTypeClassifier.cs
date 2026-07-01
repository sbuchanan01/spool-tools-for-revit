using Autodesk.Revit.DB;

namespace SpoolTools.Revit
{
    /// <summary>
    /// Maps a FabricationPart to its PCF component keyword and ISOGEN shape key (SKEY).
    ///
    /// Classification order:
    ///   1. Alias string match
    ///   2. Product long description string match (same keywords)
    ///   3. Geometry / connector-count fallback
    /// </summary>
    internal static class PartTypeClassifier
    {
        /// <summary>
        /// Fabrication catalog Item IDs (CIDs) that are definitively valves.
        /// Add additional CIDs here as they are identified.
        /// </summary>
        public static readonly HashSet<int> ValveCids = new() { 868 };

        /// <summary>
        /// Fabrication catalog Item IDs (CIDs) that are definitively straight
        /// duct sections (rectangular / round / oval / flat-oval). Used by the
        /// Hanger Layout tool to identify "joint interface between two straight
        /// ducts" without relying on length heuristics. PCF has no duct domain,
        /// so the geometric and PCF-name tests don't help here.
        /// Add additional CIDs as they are identified.
        /// </summary>
        public static readonly HashSet<int> StraightDuctCids = new()
        {
            // Rectangular duct straights
            1, 35, 866, 924,
            // Round duct straights
            40, 41,
        };

        public static bool IsStraightDuctByCid(FabricationPart part)
        {
            try { return StraightDuctCids.Contains(part.ItemCustomId); }
            catch { return false; }
        }

        public static string GetPcfType(FabricationPart part)
        {
            if (part.IsAHanger())
                return "SUPPORT";

            // Pass 1 — CID lookup (highest priority, unambiguous catalog identity)
            if (ValveCids.Contains(part.ItemCustomId))
                return "VALVE";

            // Pass 2 — alias text match
            string alias = part.Alias?.ToUpperInvariant() ?? string.Empty;
            string? result = ClassifyByText(alias);
            if (result != null) return result;

            // Pass 3 — product long description (catches catalogs where alias is a part number)
            string desc = GetDescription(part).ToUpperInvariant();
            result = ClassifyByText(desc);
            if (result != null) return result;

            // Pass 4 — check for pipe with taps (3+ connectors but still a straight pipe)
            // Pipe descriptions start with "Pipe" — no fitting/tee/valve does.
            string combined = alias + " " + desc;
            if (combined.IndexOf("PIPE", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !ContainsAny(combined, "SUPPORT", "CLAMP", "HANGER", "NIPPLE"))
                return "PIPE";

            // Pass 5 — connector-count geometry
            var connectors = ConnectorHelper.GetPhysicalConnectors(part);
            return connectors.Count switch
            {
                0 => "PIPE",
                1 => "CAP",
                2 => ClassifyTwoConnector(connectors),
                3 => "TEE",
                4 => "CROSS",
                _ => "TEE"
            };
        }

        /// <summary>
        /// Returns the ISOGEN shape key (SKEY) for the part.
        /// Format: 2-letter component prefix + 2-letter end-connection suffix.
        /// e.g. EL(component) + BW(butt weld) = ELBW
        ///      FL(flange)    + WN(weld neck)  = FLWN
        ///      VT(gate valve)+ FL(flanged)    = VTFL
        /// Tries the "Fabrication Item PCF SKEY" parameter first, then derives from
        /// PCF type, description, and connector end type.
        /// </summary>
        public static string GetSkey(FabricationPart part)
        {
            // Try explicit parameter first — case-insensitive to handle varying shared-parameter
            // definition capitalisation (e.g. "PCF Skey" vs "PCF SKEY").
            string? fromParam = FindSkeyParam(part);

            if (!string.IsNullOrWhiteSpace(fromParam))
                return fromParam;

            string pcfType       = GetPcfType(part);
            string alias         = (part.Alias ?? string.Empty).ToUpperInvariant();
            string desc          = GetDescription(part).ToUpperInvariant();
            string productName   = GetProductName(part).ToUpperInvariant();
            string combined      = alias + " " + desc + " " + productName;
            string installType   = (part.ProductInstallType ?? string.Empty).ToUpperInvariant();

            // ProductInstallType ("Flanged", "Butt Weld", "Socket Weld", etc.) is more reliable
            // than text-matching the description, so prefer it when non-empty.
            string endSuffix = DeriveEndSuffix(combined, installType);

            return pcfType switch
            {
                "ELBOW"    => "EL" + endSuffix,
                "TEE"      => "TE" + endSuffix,
                "CROSS"    => "CR" + endSuffix,
                "REDUCER"  => DeriveReducerSkey(combined, endSuffix),
                "CAP"      => "KA" + endSuffix,
                "COUPLING" => "CO" + endSuffix,
                "WELD"     => DeriveWeldSkey(combined),
                "FLANGE"   => DeriveFlangeSkey(combined),
                "VALVE"    => DeriveValveSkey(combined, endSuffix, installType),
                "OLET"     => DeriveOletSkey(combined, endSuffix),
                "SUPPORT"  => DeriveSupportSkey(combined),
                _          => string.Empty  // PIPE carries no SKEY in standard ISOGEN
            };
        }

        /// <summary>
        /// Derives the 2-letter end-connection suffix (BW, FL, SW, SC, GL, PL).
        /// Prefers the ProductInstallType property (e.g. "Flanged", "Butt Weld") when available,
        /// falls back to keyword matching in the alias + description.
        /// </summary>
        private static string DeriveEndSuffix(string combined, string installType = "")
        {
            // ProductInstallType is a direct fabrication property — more reliable than text matching
            if (!string.IsNullOrWhiteSpace(installType))
            {
                if (ContainsAny(installType, "SCREW", "THREAD"))          return "SC";
                if (ContainsAny(installType, "SOCKET"))                    return "SW";
                if (ContainsAny(installType, "GLUE", "CEMENT", "SOLVENT")) return "GL";
                if (ContainsAny(installType, "PLAIN"))                     return "PL";
                if (ContainsAny(installType, "FLANGE", "FLG"))             return "FL";
                if (ContainsAny(installType, "BUTT", "WELD", "BW"))        return "BW";
            }

            // Fallback: keyword match on alias + description text
            if (ContainsAny(combined, "SCREWED", "THREADED", "SCRD", "THRD", " SC ", "-SC")) return "SC";
            if (ContainsAny(combined, "SOCKET WELD", "SOCKT WLD", " SW ", "S/W", "-SW"))    return "SW";
            if (ContainsAny(combined, "GLUED", "CEMENTED", "SOLVENT"))                       return "GL";
            if (ContainsAny(combined, "PLAIN END", "PLAIN-END", " PE "))                     return "PL";
            if (ContainsAny(combined, "FLANGED", " FLG ", "(FLG)", "FLG."))                  return "FL";
            return "BW"; // Default: butt weld
        }

        private static string DeriveWeldSkey(string combined)
        {
            if (ContainsAny(combined, "FIELD", "SITE"))   return "WS"; // Site/Field Weld
            if (ContainsAny(combined, "FIT", "FIT-UP"))   return "WF"; // Field Fit Weld
            if (ContainsAny(combined, "OFFSHORE"))         return "WO"; // Offshore Weld
            if (ContainsAny(combined, "MITRE", "MITER"))   return "WM"; // Mitre Weld
            return "WW"; // Workshop/Shop Weld (default)
        }

        private static string DeriveReducerSkey(string combined, string endSuffix)
        {
            if (ContainsAny(combined, "ECC", "ECCENTRIC")) return "RE" + endSuffix;
            return "RC" + endSuffix; // Concentric (default)
        }

        private static string DeriveSupportSkey(string combined)
        {
            if (ContainsAny(combined, "ANCHOR"))                              return "ANCH";
            if (ContainsAny(combined, "GUIDE"))                               return "GUID";
            if (ContainsAny(combined, "SPRING"))                              return "SPRG";
            if (ContainsAny(combined, "DUCK", "DUCK FOOT"))                   return "DUCK";
            if (ContainsAny(combined, "SKID", "SHOE"))                        return "SKID";
            if (ContainsAny(combined, "STANCHION", "STAND"))                  return "01HG";
            if (ContainsAny(combined, "CLAMP", "BEAM CLAMP"))                 return "01HG";
            if (ContainsAny(combined, "CLEVIS", "HANGER", "HANG"))            return "HANG";
            return "01HG"; // Generic hanger/support
        }

        private static string DeriveOletSkey(string combined, string endSuffix)
        {
            if (ContainsAny(combined, "WELDOLET", "WELD-O-LET", "WELDO")) return "WT" + endSuffix;
            if (ContainsAny(combined, "SOCKOLET", "SOCK-O-LET", "SOCKO")) return "SK" + endSuffix;
            if (ContainsAny(combined, "THREADOLET", "THREAD-O-LET", "THREADO")) return "TH" + endSuffix;
            return "OL" + endSuffix; // Generic olet (anvilet, stub-in, etc.)
        }

        /// <summary>
        /// Flanges: second 2 letters describe the flange face/type, not the pipe end type.
        /// WN=weld neck, SO=slip-on, BL=blind, SE=socket-weld end, SC=screwed, LJ=lap joint.
        /// </summary>
        private static string DeriveFlangeSkey(string combined)
        {
            if (ContainsAny(combined, "BLIND", "FLBL"))                        return "FLBL";
            if (ContainsAny(combined, "SLIP-ON", "SLIP ON", "FLSO", "SLIP"))   return "FLSO";
            if (ContainsAny(combined, "SOCKET WELD FL", "FLSE", "FLSW"))       return "FLSE";
            if (ContainsAny(combined, "SCREW", "THREAD", "FLSC"))              return "FLSC";
            if (ContainsAny(combined, "LAP JOINT", "FLLJ"))                    return "FLLJ";
            if (ContainsAny(combined, "RING JOINT", "FLRJ", "RTJ"))            return "FLRJ";
            if (ContainsAny(combined, "SPECTACLE", "SPEC BLIND", "FIGURE 8"))  return "FLSC"; // nearest standard
            return "FLWN"; // Default: weld neck
        }

        /// <summary>
        /// Valves: V + valve-type-letter + end-connection suffix.
        /// e.g. VT(gate/through) + FL(flanged) = VTFL
        ///      VC(check)        + BW(butt)    = VCBW
        /// </summary>
        private static string DeriveValveSkey(string combined, string endSuffix, string installType = "")
        {
            string vType;
            if      (ContainsAny(combined, "CHECK", "CHCK", "NON-RETURN", "NRV")) vType = "VC";
            else if (ContainsAny(combined, "BALL"))                                vType = "VB";
            else if (ContainsAny(combined, "BUTTERFLY", "BFLY"))                   vType = "VY";
            else if (ContainsAny(combined, "GLOBE"))                               vType = "VG";
            else if (ContainsAny(combined, "NEEDLE"))                              vType = "VN";
            else if (ContainsAny(combined, "DIAPHRAGM"))                           vType = "VD";
            else if (ContainsAny(combined, "PLUG"))                                vType = "VP";
            else                                                                   vType = "VT"; // Gate/through (default)

            // End suffix is already resolved correctly via ProductInstallType or text match
            return vType + endSuffix;
        }

        /// <summary>Returns the PCF connection end type for a connector index on a component.</summary>
        public static string GetEndType(string pcfType, string alias, int connectorIndex)
        {
            alias = alias?.ToUpperInvariant() ?? string.Empty;

            return pcfType switch
            {
                "PIPE"    => "PL",
                "WELD"    => "BW",
                "ELBOW"   => "BW",
                "TEE"     => "BW",
                "CROSS"   => "BW",
                "REDUCER" => "BW",
                "CAP"     => connectorIndex == 0 ? "PL" : "BW",
                "FLANGE"  => GetFlangeEndType(alias, connectorIndex),
                "VALVE"   => GetValveEndType(alias),
                "COUPLING"=> "BW",
                "OLET"    => "BW",
                _         => "BW"
            };
        }

        /// <summary>Returns true when the part is a straight pipe segment (not a fitting).</summary>
        public static bool IsStraightPipe(FabricationPart part)
        {
            string alias = part.Alias?.ToUpperInvariant() ?? string.Empty;
            string desc  = GetDescription(part).ToUpperInvariant();

            if (ContainsAny(alias + " " + desc, "STR", "STRAIGHT"))
                return true;

            // "PIPE" keyword — only treat as straight pipe if NOT also a fitting keyword
            string combined = alias + " " + desc;
            if (combined.Contains("PIPE", StringComparison.OrdinalIgnoreCase)
                && !ContainsAny(combined, "NIPPLE", "SWAGE"))
                return true;

            // Geometry: 2 connectors, anti-parallel, same bore
            var connectors = ConnectorHelper.GetPhysicalConnectors(part);
            if (connectors.Count != 2) return false;

            double r0 = connectors[0].Radius;
            double r1 = connectors[1].Radius;
            if (Math.Abs(r0 - r1) > 0.001) return false;

            var d0 = connectors[0].CoordinateSystem.BasisZ;
            var d1 = connectors[1].CoordinateSystem.BasisZ;
            return Math.Abs(d0.DotProduct(d1)) > 0.999;
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Attempts to classify a part from a text string (alias or description).
        /// Returns null if no keyword match is found.
        /// </summary>
        private static string? ClassifyByText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // FLANGE must be checked before WELD — "Weld Neck Flange" contains "WELD"
            if (ContainsAny(text, "FLANGE", "FLG", "FLNG", "FLWN", "FLSO", "FLBL",
                                  "WELD NECK", "SLIP-ON", "SLIP ON", "BLIND FL",
                                  "SOCKET WELD FL", "THREADED FLANGE", "LAP JOINT"))
                return "FLANGE";
            // WELD: require "SHOP WELD", "FIELD WELD", "BUTT WELD", or the "WLD" abbreviation
            // to avoid matching "Weld Neck" on flanges
            if (ContainsAny(text, "WLD", "SHOP WELD", "FIELD WELD", "BUTT WELD", "SHOP-WELD", "FIELD-WELD"))
                return "WELD";
            if (ContainsAny(text, "ELBOW", "ELB", "BEND", " LR ", " SR ", "LR-", "SR-", "90 LR", "45 LR", "90-LR", "45-LR"))
                return "ELBOW";
            if (ContainsAny(text, "TEE", "T-EE"))
                return "TEE";
            if (ContainsAny(text, "CROSS", "WYE", "LATERAL"))
                return "CROSS";
            if (ContainsAny(text, "REDUCER", "REDUCING", "CONC.", "ECC.", "CONCENTRIC", "ECCENTRIC", "TRANSITION"))
                return "REDUCER";
            if (ContainsAny(text, "CAP", "BLIND CAP", "END CAP"))
                return "CAP";
            if (ContainsAny(text, "VALVE", "VLV", "GATE V", "BALL V", "CHECK V", "BUTTERFLY V",
                                  "GLOBE V", "CONTROL V", "RELIEF V", "VCFL", "VTFL", "VGF",
                                  "CHCK", "BFLY", "PLUG V"))
                return "VALVE";
            if (ContainsAny(text, "COUPLING", "UNION", " CPL"))
                return "COUPLING";
            if (ContainsAny(text, "OLET", "O-LET", "WELDOLET", "SOCKOLET", "THREADOLET",
                                  "ANVILET", "STUB-IN", "STAB-IN", "STUB IN", "STAB IN"))
                return "OLET";
            if (ContainsAny(text, "PLUG"))
                return "CAP";

            return null;
        }

        private static string ClassifyTwoConnector(List<Connector> connectors)
        {
            double r0 = connectors[0].Radius;
            double r1 = connectors[1].Radius;
            if (Math.Abs(r0 - r1) > 0.001)
                return "REDUCER";

            var    d0  = connectors[0].CoordinateSystem.BasisZ;
            var    d1  = connectors[1].CoordinateSystem.BasisZ;
            double dot = Math.Abs(d0.DotProduct(d1));
            return dot > 0.999 ? "PIPE" : "ELBOW";
        }

        private static string GetFlangeEndType(string alias, int connectorIndex)
        {
            if (connectorIndex == 1) return "FL";
            if (ContainsAny(alias, "FLSO", "SLIP", "SO-", "SW-")) return "SW";
            return "BW";
        }

        private static string GetValveEndType(string alias)
        {
            if (ContainsAny(alias, "FLG", "FLNG", "FL", "VCFL", "VTFL", "VGF"))
                return "FL";
            return "BW";
        }

        /// <summary>Reads the product long description from common parameter names.</summary>
        private static string GetDescription(FabricationPart part)
        {
            return part.LookupParameter("Long Description")?.AsString()
                ?? part.LookupParameter("Product Long Description")?.AsString()
                ?? part.LookupParameter("Description")?.AsString()
                ?? part.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString()
                ?? string.Empty;
        }

        /// <summary>
        /// Reads the product name (e.g. "Check Valve", "Gate Valve") from the
        /// ProductName property and common parameter name variants.
        /// </summary>
        private static string GetProductName(FabricationPart part)
        {
            return part.LookupParameter("Product Name")?.AsString()
                ?? part.LookupParameter("ProductName")?.AsString()
                ?? string.Empty;
        }

        /// <summary>
        /// Looks for a SKEY parameter value using a case-insensitive name search.
        /// LookupParameter() is case-sensitive and will miss "PCF Skey" when searching "PCF SKEY".
        /// </summary>
        private static string? FindSkeyParam(FabricationPart part)
        {
            // Fast path — try every known capitalisation variant of the parameter name.
            // LookupParameter() is case-sensitive, so each variant must be listed explicitly.
            string[] candidateNames =
            {
                "PCF Skey",                  // confirmed name seen in Revit UI
                "PCF SKEY",
                "Fabrication Item PCF SKEY",
                "Fabrication Item PCF Skey",
                "SKEY",
                "Skey",
            };

            foreach (string name in candidateNames)
            {
                string? v = part.LookupParameter(name)?.AsString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }

            // Slow path — case-insensitive scan for any parameter whose name contains "SKEY".
            // Covers fabrication content parameters that appear in the Parameters collection.
            foreach (Parameter p in part.Parameters)
            {
                if (p.Definition.Name.IndexOf("SKEY", StringComparison.OrdinalIgnoreCase) >= 0
                    && p.StorageType == StorageType.String)
                {
                    string? val = p.AsString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
            return null;
        }

        private static bool ContainsAny(string source, params string[] tokens)
            => tokens.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
