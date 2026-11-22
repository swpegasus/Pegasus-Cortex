using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Serialization;

namespace Pegasus.Cortex.LearningExports.DefaultLibrary;

/// <summary>
/// Converts one immutable Default Library revision without granting export authority.
/// TrainingEligibility is deliberately ignored; a separate review and runtime grant remain required.
/// </summary>
public static class DefaultLibraryLearningSourceAdapter
{
    public static LearningRecordRevisionV1 FromKnowledgeRecord(
        KnowledgeRecordV1 record,
        LearningExportReviewV1 review,
        string sourceServiceId,
        string sourceArtifactId,
        string leakageGroupId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(review);
        return LearningExportFactory.CreateRecord(
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            "reviewed_knowledge",
            sourceServiceId,
            sourceArtifactId,
            leakageGroupId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content"] = record.Content,
                ["summary"] = record.Summary,
                ["taxonomy"] = record.Taxonomy,
                ["title"] = record.Title
            },
            record.Sensitivity,
            review,
            record.CreatedAt,
            record.ContentHash);
    }
}
