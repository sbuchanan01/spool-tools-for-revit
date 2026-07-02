using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpoolTools.Revit
{
    /// <summary>
    /// Runtime icon generator for ribbon buttons. Uses
    /// <see cref="DrawingVisual"/> + <see cref="RenderTargetBitmap"/>
    /// to render vector glyphs into 16/32 px ImageSources at startup —
    /// avoids shipping bitmap files alongside the DLL and keeps the
    /// icons crisp at both ribbon image sizes (small / large).
    ///
    /// Glyph source: <b>Segoe MDL2 Assets</b> (bundled with Windows 10+)
    /// for character-based icons — purpose-built UI iconography with
    /// thousands of glyphs that scale cleanly. Falls back to vector
    /// shape drawing (StreamGeometry) for icons that don't have a clean
    /// character equivalent (hangers, sprinklers, iso cubes).
    /// </summary>
    internal static class RibbonIconFactory
    {
        // ── Color palette ──────────────────────────────────────────────────
        // Picked to read as the icon's semantic role without clashing
        // with Revit's light ribbon chrome. Saturation pulled back from
        // pure primaries so the icons feel native rather than neon.

        private static readonly Color MoneyGreen   = Color.FromRgb(0x1B, 0x8E, 0x2F); // $
        private static readonly Color ActionBlue   = Color.FromRgb(0x2D, 0x6B, 0x9E); // settings / import
        private static readonly Color SyncTeal     = Color.FromRgb(0x1E, 0x9D, 0x9D); // sync / cycle
        private static readonly Color ForestGreen  = Color.FromRgb(0x2E, 0x8B, 0x2E); // refresh / export
        private static readonly Color ReportOrange = Color.FromRgb(0xD2, 0x69, 0x1E); // estimate / iso
        private static readonly Color TagAmber     = Color.FromRgb(0xDA, 0xA5, 0x20); // tag
        private static readonly Color WaterBlue    = Color.FromRgb(0x4A, 0x90, 0xE2); // sprinkler
        private static readonly Color MetalGray    = Color.FromRgb(0x55, 0x55, 0x55); // settings / wrench / hanger
        private static readonly Color ToolGray     = Color.FromRgb(0x46, 0x59, 0x6E); // diagnostics
        private static readonly Color PipeSteel    = Color.FromRgb(0x4E, 0x73, 0x99); // spool
        private static readonly Color WarnRed      = Color.FromRgb(0xB0, 0x3A, 0x2E); // wipe / destructive
        private static readonly Color BurstYellow  = Color.FromRgb(0xF5, 0xC5, 0x18); // explosion core

        // ── Public API ─────────────────────────────────────────────────────
        // One method per ribbon icon; each returns a frozen ImageSource
        // ready to assign to PushButtonData.LargeImage / Image.

        /// <summary>Block-style "$" — Cost Breakdown.</summary>
        public static ImageSource DollarSign(int size) =>
            Render(size, (dc, s) => DrawText(dc, s, "$",
                fontFamily: "Arial Black", weight: FontWeights.Black,
                color: MoneyGreen, sizeFraction: 0.95));

        /// <summary>Gear / cog — Pricing Setup.</summary>
        public static ImageSource Gear(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ActionBlue));

        /// <summary>Sync arrows — Pricing Sync.</summary>
        public static ImageSource SyncArrows(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", SyncTeal));

        /// <summary>Report / document — Generate Estimate pulldown.</summary>
        public static ImageSource ReportDocument(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ReportOrange));

        /// <summary>Refresh arrow — Refresh from PCF.</summary>
        public static ImageSource Refresh(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ForestGreen));

        /// <summary>Tag — Assign Tag.</summary>
        public static ImageSource Tag(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", TagAmber));

        /// <summary>Upload / outgoing arrow — Export pulldown.</summary>
        public static ImageSource Upload(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ForestGreen));

        /// <summary>Download / incoming arrow — Import pulldown.</summary>
        public static ImageSource Download(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ActionBlue));

        /// <summary>Wrench — Settings pulldown.</summary>
        public static ImageSource Wrench(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", MetalGray));

        /// <summary>Magnifying glass — Diagnostics pulldown.</summary>
        public static ImageSource MagnifyingGlass(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ToolGray));

        /// <summary>Water drop — Sprinkler Layout (drawn shape; no clean
        /// Segoe MDL2 glyph for sprinklers).</summary>
        public static ImageSource WaterDrop(int size) =>
            Render(size, DrawWaterDrop);

        /// <summary>Ring hanger — Hanger Layout (drawn shape; no clean
        /// Segoe MDL2 glyph for pipe hangers). Vertical drop rod
        /// terminating at a circular band that cradles a pipe or round
        /// duct — reads more clearly at 16px ribbon scale than a J-hook
        /// and matches the round-hanger profile that drives the
        /// shape-compatibility filter on this tool.</summary>
        public static ImageSource Hanger(int size) =>
            Render(size, DrawHanger);

        /// <summary>Three-face isometric cube — Generate Iso (SVG)
        /// (drawn shape; gives a true ISOGEN look instead of a generic
        /// 3D-object glyph).</summary>
        public static ImageSource IsoCube(int size) =>
            Render(size, DrawIsoCube);

        /// <summary>Pipe-elbow-pipe silhouette — Create Spool. Horizontal
        /// pipe leg turning 90° up to a vertical pipe leg, with small
        /// perpendicular caps at each open end so the shape reads as a
        /// fab spool with prepared ends rather than a generic L.</summary>
        public static ImageSource Spool(int size) =>
            Render(size, DrawSpool);

        /// <summary>Two offset pipe spools with "1" and "2" badges
        /// overlaid — the ribbon icon for The Spooler (batch / multi-
        /// spool tool). Differentiates from Create Spool's single-spool
        /// glyph by literally showing two spools in sequence with their
        /// per-spool numbering.</summary>
        public static ImageSource Spooler(int size) =>
            Render(size, DrawSpooler);

        // Three Segoe MDL2 glyphs below use \u escapes rather than
        // literal PUA characters — file-write paths sometimes strip
        // out-of-BMP-ish chars silently, leaving an empty glyph string
        // and a blank icon. Codepoints picked from the base MDL2 range
        // (E000-EAFF) so they ship on every Win10+ install.

        /// <summary>Camera glyph (Segoe MDL2 U+E722) — Snapshot Config
        /// (captures the current configuration state to a baseline
        /// file on disk).</summary>
        public static ImageSource Camera(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", MetalGray));

        /// <summary>Copy / two-papers glyph (Segoe MDL2 U+E8C8) — Compare
        /// to Snapshot. Two stacked rectangles read naturally as "two
        /// versions side by side".</summary>
        public static ImageSource Compare(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ToolGray));

        /// <summary>Repair / wrench-on-circle glyph (Segoe MDL2 U+E895)
        /// — Reload Config (Tracked). Reads as "config maintenance with
        /// checks", which is the workflow this button kicks off.</summary>
        public static ImageSource ReloadCheck(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ForestGreen));

        /// <summary>Eraser glyph (Segoe MDL2 U+E75C / EraseTool) -
        /// Pricing Wipe. Red color flags the destructive action; the
        /// eraser metaphor reads as "clear values" without being as
        /// harsh as a trash can or X (which would imply Delete, not
        /// Reset). Pricing Sync can restore the values any time.</summary>
        public static ImageSource Eraser(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", WarnRed));

        /// <summary>Settings-gear glyph (Segoe MDL2 U+E713) in the
        /// PipeSteel palette — Spool Config. Same gear visual as the
        /// generic Settings icon but colour-keyed to the Spooling panel
        /// so it visually groups with Create Spool / The Spooler rather
        /// than reading as a global tool.</summary>
        /// <summary>Same elbow-spool base as <see cref="Spool"/> but with
        /// the two pipe legs pulled back from the elbow (short gaps) and
        /// a red "pow" starburst overlaid on the elbow — reads as "the
        /// spool is being blown apart", matching what the command
        /// actually does. Drawn glyph rather than a Segoe MDL2 character
        /// because the eraser PUA codepoint gets stripped on some write
        /// paths and renders blank; a StreamGeometry never risks that.
        /// </summary>
        public static ImageSource DeSpool(int size) =>
            Render(size, DrawDeSpool);

        public static ImageSource SpoolGear(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", PipeSteel));

        /// <summary>Cloud-with-upload-arrow glyph (Segoe MDL2 U+EBD3,
        /// CloudUpload) in the ActionBlue palette — Send to Cost
        /// Management. Reads as "push local data up to a cloud service",
        /// which is exactly what this button does (Revit estimate →
        /// ACC Forma Cost Management Expenses). \u escape form matches
        /// the safer-PUA pattern documented above for Camera / Compare /
        /// ReloadCheck / Eraser.</summary>
        public static ImageSource CloudUpload(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ActionBlue));

        // ── Shared rendering plumbing ──────────────────────────────────────

        /// <summary>Common render scaffolding: open a DrawingVisual, call
        /// the per-icon <paramref name="draw"/> action, rasterize to a
        /// frozen ARGB bitmap at 96 DPI.</summary>
        private static ImageSource Render(int size, Action<DrawingContext, int> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                draw(dc, size);
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();   // immutable → safe to assign to any UI thread
            return bmp;
        }

        /// <summary>Renders a single Segoe MDL2 Assets glyph centered in
        /// the icon box at ~85% of the available height. That sizing
        /// leaves a small breathing margin so the glyph doesn't kiss
        /// adjacent ribbon items at 32 px.</summary>
        private static void DrawGlyph(DrawingContext dc, int size,
                                      string text, Color color) =>
            DrawText(dc, size, text,
                fontFamily: "Segoe MDL2 Assets",
                weight: FontWeights.Normal,
                color: color,
                sizeFraction: 0.85);

        private static void DrawText(DrawingContext dc, int size, string text,
            string fontFamily, FontWeight weight, Color color, double sizeFraction)
        {
            var typeface = new Typeface(
                new FontFamily(fontFamily),
                FontStyles.Normal, weight, FontStretches.Normal);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var ft = new FormattedText(
                text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * sizeFraction,
                brush,
                pixelsPerDip: 1.0);
            double x = (size - ft.Width)  / 2.0;
            double y = (size - ft.Height) / 2.0;
            dc.DrawText(ft, new Point(x, y));
        }

        // ── Hand-drawn shape icons ─────────────────────────────────────────

        /// <summary>Teardrop-shaped water drop. Two cubic Béziers form
        /// the bulb; a single point at the top gives the classic drop
        /// silhouette. Filled, no outline.</summary>
        private static void DrawWaterDrop(DrawingContext dc, int size)
        {
            double w = size, h = size;
            double cx = w * 0.5;
            double topY = h * 0.10;
            double botY = h * 0.88;
            double waist = h * 0.55;
            double radius = w * 0.32;

            var brush = new SolidColorBrush(WaterBlue);
            brush.Freeze();

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(cx, topY), isFilled: true, isClosed: true);
                // Right side: from top point, curve out to right shoulder,
                // then sweep down to the bottom point.
                ctx.BezierTo(
                    new Point(cx + radius * 0.4, waist * 0.55),
                    new Point(cx + radius,       waist),
                    new Point(cx, botY),
                    isStroked: true, isSmoothJoin: false);
                // Mirror back up the left side.
                ctx.BezierTo(
                    new Point(cx - radius,       waist),
                    new Point(cx - radius * 0.4, waist * 0.55),
                    new Point(cx, topY),
                    isStroked: true, isSmoothJoin: false);
            }
            geom.Freeze();
            dc.DrawGeometry(brush, null, geom);
        }

        /// <summary>Pipe hanger silhouette — vertical drop rod with a
        /// J-hook curve at the bottom. Stroked path, not filled, so the
        /// shape reads as a metal support rather than a blob.</summary>
        private static void DrawHanger(DrawingContext dc, int size)
        {
            double w = size, h = size;
            double strokeWidth = Math.Max(1.5, size * 0.10);
            var pen = new Pen(new SolidColorBrush(MetalGray), strokeWidth)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
                LineJoin     = PenLineJoin.Round,
            };
            pen.Brush.Freeze();
            pen.Freeze();

            // Ring hanger: a vertical drop rod from near the top of the
            // canvas, terminating at a stroked circle that represents the
            // hanger band. Symmetric and centered — reads cleanly at 16px.
            double cx           = w * 0.50;
            double rodTopY      = h * 0.05;
            double ringRadius   = w * 0.32;
            double ringCenterY  = h * 0.62;
            double ringTopY     = ringCenterY - ringRadius;

            // Drop rod (top to top-of-ring).
            var rodGeom = new StreamGeometry();
            using (var ctx = rodGeom.Open())
            {
                ctx.BeginFigure(new Point(cx, rodTopY), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(cx, ringTopY), isStroked: true, isSmoothJoin: false);
            }
            rodGeom.Freeze();
            dc.DrawGeometry(null, pen, rodGeom);

            // Hanger band (stroked circle, no fill — keeps the silhouette
            // crisp at small ribbon scales).
            var ring = new EllipseGeometry(new Point(cx, ringCenterY), ringRadius, ringRadius);
            ring.Freeze();
            dc.DrawGeometry(null, pen, ring);
        }

        /// <summary>Three-face isometric cube. Top diamond + left and
        /// right rhombuses, lightly value-stepped so the three faces
        /// read as distinct planes (matches the ISOGEN look our iso
        /// generator targets).</summary>
        private static void DrawIsoCube(DrawingContext dc, int size)
        {
            double cx = size / 2.0;
            double cy = size / 2.0;
            double s  = size * 0.34;     // half-edge of the cube projection

            // Slightly lighter top, midtone right, darker left — classic
            // 3-tone iso shading without going overboard on contrast.
            var topBrush   = FreezeBrush(Color.FromRgb(0xF0, 0xA8, 0x55));
            var rightBrush = FreezeBrush(Color.FromRgb(0xD2, 0x69, 0x1E));
            var leftBrush  = FreezeBrush(Color.FromRgb(0xA0, 0x4E, 0x16));
            var edgePen    = new Pen(FreezeBrush(Color.FromRgb(0x55, 0x2E, 0x0A)),
                                     Math.Max(1.0, size * 0.04));
            edgePen.Freeze();

            // Vertices of the three visible faces. Center of the cube is
            // at (cx, cy); 's' controls overall size.
            var top    = new Point(cx,         cy - s);
            var right  = new Point(cx + s,     cy - s * 0.5);
            var bottom = new Point(cx,         cy + s);
            var left   = new Point(cx - s,     cy - s * 0.5);
            var midRight  = new Point(cx + s,  cy + s * 0.5);
            var midLeft   = new Point(cx - s,  cy + s * 0.5);
            var center    = new Point(cx,      cy);

            DrawQuad(dc, topBrush,   edgePen, top,    right,    center,  left);
            DrawQuad(dc, rightBrush, edgePen, right,  midRight, bottom,  center);
            DrawQuad(dc, leftBrush,  edgePen, left,   center,   bottom,  midLeft);
        }

        private static void DrawQuad(DrawingContext dc, Brush fill, Pen edge,
            Point p1, Point p2, Point p3, Point p4)
        {
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(p1, isFilled: true, isClosed: true);
                ctx.LineTo(p2, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(p3, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(p4, isStroked: true, isSmoothJoin: false);
            }
            geom.Freeze();
            dc.DrawGeometry(fill, edge, geom);
        }

        /// <summary>Two-leg pipe spool with a 90° elbow. Drawn as a single
        /// stroked path (centerline) plus two perpendicular cap bars at the
        /// open ends. Sweep direction Counterclockwise so the elbow bulges
        /// toward the outer corner of the L (matches how a real elbow
        /// fitting curves around a turn).</summary>
        private static void DrawSpool(DrawingContext dc, int size)
        {
            double w = size, h = size;
            double pipeWidth = Math.Max(2.0, size * 0.20);

            var pipePen = new Pen(FreezeBrush(PipeSteel), pipeWidth)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap   = PenLineCap.Flat,
                LineJoin     = PenLineJoin.Round,
            };
            pipePen.Freeze();

            double leftX   = w * 0.14;
            double bottomY = h * 0.76;
            double elbowX  = w * 0.66;
            double topY    = h * 0.14;
            double elbowR  = w * 0.14;

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(leftX, bottomY), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(elbowX - elbowR, bottomY),
                    isStroked: true, isSmoothJoin: false);
                ctx.ArcTo(
                    new Point(elbowX, bottomY - elbowR),
                    new Size(elbowR, elbowR),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: SweepDirection.Counterclockwise,
                    isStroked: true,
                    isSmoothJoin: false);
                ctx.LineTo(new Point(elbowX, topY),
                    isStroked: true, isSmoothJoin: false);
            }
            geom.Freeze();
            dc.DrawGeometry(null, pipePen, geom);

            // Flange caps — slightly thinner stroke, length ~= 1.4 × pipe
            // width so they project past the pipe wall and read as flanges.
            double capHalf = pipeWidth * 0.85;
            var capPen = new Pen(FreezeBrush(PipeSteel), Math.Max(1.5, size * 0.07))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
            };
            capPen.Freeze();

            dc.DrawLine(capPen,
                new Point(leftX, bottomY - capHalf),
                new Point(leftX, bottomY + capHalf));
            dc.DrawLine(capPen,
                new Point(elbowX - capHalf, topY),
                new Point(elbowX + capHalf, topY));
        }

        /// <summary>DeSpooler variant of <see cref="DrawSpool"/>. Same
        /// three-piece layout (horizontal leg + elbow + vertical leg)
        /// with a big visible gap between each leg and the elbow, plus
        /// a red "prohibited" ring (circle + diagonal slash) overlaid on
        /// the assembly.</summary>
        private static void DrawDeSpool(DrawingContext dc, int size)
        {
            double w = size, h = size;
            double pipeWidth = Math.Max(2.0, size * 0.18);

            var pipePen = new Pen(FreezeBrush(PipeSteel), pipeWidth)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap   = PenLineCap.Flat,
                LineJoin     = PenLineJoin.Round,
            };
            pipePen.Freeze();

            double leftX   = w * 0.14;
            double bottomY = h * 0.76;
            double elbowX  = w * 0.66;
            double topY    = h * 0.14;
            double elbowR  = w * 0.14;

            double gap = Math.Max(3.5, size * 0.13);

            var elbowGeom = new StreamGeometry();
            using (var ctx = elbowGeom.Open())
            {
                ctx.BeginFigure(
                    new Point(elbowX - elbowR, bottomY),
                    isFilled: false, isClosed: false);
                ctx.ArcTo(
                    new Point(elbowX, bottomY - elbowR),
                    new Size(elbowR, elbowR),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: SweepDirection.Counterclockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
            elbowGeom.Freeze();
            dc.DrawGeometry(null, pipePen, elbowGeom);

            dc.DrawLine(pipePen,
                new Point(leftX, bottomY),
                new Point(elbowX - elbowR - gap, bottomY));
            dc.DrawLine(pipePen,
                new Point(elbowX, topY),
                new Point(elbowX, bottomY - elbowR - gap));

            double capHalf = pipeWidth * 0.85;
            var capPen = new Pen(FreezeBrush(PipeSteel), Math.Max(1.5, size * 0.07))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
            };
            capPen.Freeze();
            dc.DrawLine(capPen,
                new Point(leftX, bottomY - capHalf),
                new Point(leftX, bottomY + capHalf));
            dc.DrawLine(capPen,
                new Point(elbowX - capHalf, topY),
                new Point(elbowX + capHalf, topY));

            // Prohibited ring (⊘) centred on the elbow's outside corner
            // so it visually wraps the whole elbow + adjacent gaps.
            double ringCX = elbowX - elbowR * 0.5;
            double ringCY = bottomY - elbowR * 0.5;
            double ringR  = size * 0.30;

            var ringBrush = FreezeBrush(WarnRed);
            var ringPen = new Pen(ringBrush, Math.Max(2.0, size * 0.08))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
                LineJoin     = PenLineJoin.Round,
            };
            ringPen.Freeze();

            dc.DrawEllipse(
                null, ringPen,
                new Point(ringCX, ringCY),
                ringR, ringR);

            double half = ringR / Math.Sqrt(2.0);
            dc.DrawLine(ringPen,
                new Point(ringCX - half, ringCY - half),
                new Point(ringCX + half, ringCY + half));
        }

        /// <summary>Two offset elbow spools stacked diagonally so the
        /// glyph reads as "more than one spool", each badged with its
        /// sequence number (1, 2). Spool 1 sits in the lower-left, spool
        /// 2 in the upper-right with a small overlap that suggests
        /// "spool 1 leads into spool 2" without the strokes literally
        /// touching.</summary>
        private static void DrawSpooler(DrawingContext dc, int size)
        {
            double w = size, h = size;
            // Slightly thinner pipes than the single-spool icon — two of
            // them have to share the same box, so each gets a smaller
            // footprint.
            double pipeWidth = Math.Max(1.5, size * 0.13);

            var pipePen = new Pen(FreezeBrush(PipeSteel), pipeWidth)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap   = PenLineCap.Flat,
                LineJoin     = PenLineJoin.Round,
            };
            pipePen.Freeze();

            // Cap pen for flange ticks at each open end.
            var capPen = new Pen(FreezeBrush(PipeSteel), Math.Max(1.0, size * 0.05))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
            };
            capPen.Freeze();
            double capHalf = pipeWidth * 0.85;

            // Spool 1 — lower-left L, opening down-and-left.
            DrawSpoolerL(dc, pipePen, capPen, capHalf,
                cornerX: w * 0.50, cornerY: h * 0.62,
                legX:    w * 0.08, legY:    h * 0.98,
                elbowR:  w * 0.10);

            // Spool 2 — upper-right L, opening up-and-right. Offset so
            // it doesn't sit directly on top of spool 1.
            DrawSpoolerL(dc, pipePen, capPen, capHalf,
                cornerX: w * 0.50, cornerY: h * 0.38,
                legX:    w * 0.92, legY:    h * 0.02,
                elbowR:  w * 0.10);

            // Number badges — small filled circles with "1" / "2"
            // overlaid. Sized to read at 16 px (where the digits are at
            // their smallest). Positioned on the inside of each L so
            // they overlap the spool stroke and clearly belong to it.
            DrawBadge(dc, w * 0.30, h * 0.78, size * 0.22, "1");
            DrawBadge(dc, w * 0.70, h * 0.22, size * 0.22, "2");
        }

        /// <summary>Helper for one L-shaped spool segment used by the
        /// Spooler icon — straight leg + 90° elbow + perpendicular leg,
        /// with flange caps at each open end. The corner is the elbow
        /// centre; the two legs end at <c>(legX, cornerY)</c> and
        /// <c>(cornerX, legY)</c> respectively.</summary>
        private static void DrawSpoolerL(DrawingContext dc,
            Pen pipePen, Pen capPen, double capHalf,
            double cornerX, double cornerY,
            double legX, double legY,
            double elbowR)
        {
            // Direction signs so the elbow arc curves toward the corner.
            int sxLeg = legX < cornerX ? -1 : 1;   // horizontal leg direction
            int syLeg = legY < cornerY ? -1 : 1;   // vertical leg direction

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(legX, cornerY), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(cornerX + sxLeg * elbowR, cornerY),
                    isStroked: true, isSmoothJoin: false);
                ctx.ArcTo(
                    new Point(cornerX, cornerY + syLeg * elbowR),
                    new Size(elbowR, elbowR),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: (sxLeg * syLeg) < 0
                        ? SweepDirection.Counterclockwise
                        : SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
                ctx.LineTo(new Point(cornerX, legY),
                    isStroked: true, isSmoothJoin: false);
            }
            geom.Freeze();
            dc.DrawGeometry(null, pipePen, geom);

            // Flange caps perpendicular to each leg's open end.
            dc.DrawLine(capPen,
                new Point(legX, cornerY - capHalf),
                new Point(legX, cornerY + capHalf));
            dc.DrawLine(capPen,
                new Point(cornerX - capHalf, legY),
                new Point(cornerX + capHalf, legY));
        }

        /// <summary>Draws a filled circle with a centered numeric label —
        /// the "1" / "2" badges on the Spooler icon. Uses MoneyGreen
        /// for the badge fill so it pops off the steel-blue pipes, and
        /// white text for the digit so it stays legible at 16 px.</summary>
        private static void DrawBadge(DrawingContext dc, double cx, double cy,
                                      double diameter, string text)
        {
            var fill   = FreezeBrush(MoneyGreen);
            var stroke = FreezeBrush(Colors.White);
            var penOuter = new Pen(stroke, Math.Max(1.0, diameter * 0.10));
            penOuter.Freeze();

            dc.DrawEllipse(fill, penOuter,
                new Point(cx, cy), diameter * 0.5, diameter * 0.5);

            // Center the digit visually. WPF's FormattedText baseline +
            // height handling makes pixel-perfect centering finicky;
            // empirical nudge ~ 5% of diameter downward to compensate
            // for the cap-height offset from the geometric center.
            var typeface = new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal,
                FontWeights.Bold,
                FontStretches.Normal);
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                diameter * 0.72,
                stroke,
                pixelsPerDip: 1.0);
            dc.DrawText(ft, new Point(
                cx - ft.Width  * 0.5,
                cy - ft.Height * 0.5 + diameter * 0.04));
        }

        private static SolidColorBrush FreezeBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
