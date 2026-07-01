using Autodesk.Revit.DB;
using Autodesk.Revit.DB;

namespace SpoolTools.Revit
{
    /// <summary>
    /// Utility methods for working with FabricationPart connectors.
    /// </summary>
    internal static class ConnectorHelper
    {
        /// <summary>
        /// Returns physical (End/Curve) connectors for a fabrication part,
        /// excluding logical connectors.
        /// </summary>
        public static List<Connector> GetPhysicalConnectors(FabricationPart part)
        {
            var result = new List<Connector>();
            var mgr = part.ConnectorManager;
            if (mgr == null) return result;

            foreach (Connector c in mgr.Connectors)
            {
                if (c.ConnectorType is ConnectorType.End or ConnectorType.Curve)
                    result.Add(c);
            }

            return result;
        }

        /// <summary>
        /// Computes the geometric bend center for an elbow from its two end connectors.
        /// Uses line-intersection of the inward directions.
        /// Falls back to origin midpoint for degenerate/parallel cases.
        /// </summary>
        public static XYZ ComputeElbowCenter(Connector c0, Connector c1)
        {
            XYZ p0 = c0.Origin;
            XYZ p1 = c1.Origin;

            // Connector BasisZ points outward; negate for inward direction toward bend
            XYZ d0 = c0.CoordinateSystem.BasisZ.Negate();
            XYZ d1 = c1.CoordinateSystem.BasisZ.Negate();

            // Find closest approach parameter t on line p0 + t*d0
            XYZ cross = d0.CrossProduct(d1);
            double denom = cross.DotProduct(cross);

            if (denom < 1e-10)
            {
                // Directions are parallel (degenerate) — use midpoint
                return new XYZ(
                    (p0.X + p1.X) / 2,
                    (p0.Y + p1.Y) / 2,
                    (p0.Z + p1.Z) / 2);
            }

            XYZ diff = p1 - p0;
            double t = diff.CrossProduct(d1).DotProduct(cross) / denom;
            return p0 + d0.Multiply(t);
        }
    }
}
