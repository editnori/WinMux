using SelfContainedDeployment.Shell;

namespace SelfContainedDeployment
{
    internal sealed class DiffReviewSourceOption
    {
        public DiffReviewSourceKind Kind { get; init; }

        public string CheckpointId { get; init; }

        public string Label { get; init; }
    }
}
