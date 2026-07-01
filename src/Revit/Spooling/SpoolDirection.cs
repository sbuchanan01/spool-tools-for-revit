using System;

namespace SpoolTools.Revit.Spooling
{
    public enum SpoolDirection
    {
        Top   = 0,
        Front = 1,
        Left  = 2,
        Right = 3,
        SwIso = 4,
        SeIso = 5,
        NwIso = 6,
        NeIso = 7,
    }

    public enum SpoolViewKind
    {
        Plan,    // Top  (ViewFamily.FloorPlan)
        Section, // Front / Left / Right
        ThreeD,  // SW / SE / NW / NE iso
    }

    public static class SpoolDirectionExtensions
    {
        public static SpoolViewKind Kind(this SpoolDirection d) => d switch
        {
            SpoolDirection.Top   => SpoolViewKind.Plan,
            SpoolDirection.Front => SpoolViewKind.Section,
            SpoolDirection.Left  => SpoolViewKind.Section,
            SpoolDirection.Right => SpoolViewKind.Section,
            _                    => SpoolViewKind.ThreeD,
        };

        public static string Label(this SpoolDirection d) => d switch
        {
            SpoolDirection.Top   => "Top",
            SpoolDirection.Front => "Front",
            SpoolDirection.Left  => "Left",
            SpoolDirection.Right => "Right",
            SpoolDirection.SwIso => "SW Iso",
            SpoolDirection.SeIso => "SE Iso",
            SpoolDirection.NwIso => "NW Iso",
            SpoolDirection.NeIso => "NE Iso",
            _ => d.ToString(),
        };

        public static string ViewNameSuffix(this SpoolDirection d) => d switch
        {
            SpoolDirection.SwIso => "SW",
            SpoolDirection.SeIso => "SE",
            SpoolDirection.NwIso => "NW",
            SpoolDirection.NeIso => "NE",
            _ => d.ToString(),
        };
    }
}
