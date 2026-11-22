namespace Pegasus.Cortex.Core.Knowledge.Geometry;

/// <summary>Derivable point metadata only; neither tokens nor canonical-content proof.</summary>
public sealed record CortexKnowledgePointDescriptor(string ExtractorVersion, int Terms);

public static partial class CortexKnowledgeGeometry
{
    public const string PointExtractorVersion = "unicode-title4-tags6-summary1/1";
    public static CortexKnowledgePointDescriptor DescribePoint(CortexKnowledgeGeometryPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.Title.Length > 512 || point.Summary.Length > 4096 ||
            point.Tags.Count > 16 || point.Tags.Any(tag => tag.Length > 128))
            throw new ArgumentException("Point exceeds the observational projection budget.");
        return new(PointExtractorVersion, BuildVector(point.Title, point.Tags, point.Summary).Count);
    }
}
