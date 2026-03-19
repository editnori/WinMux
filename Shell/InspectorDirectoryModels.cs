using System;
using System.Collections.Generic;
using SelfContainedDeployment.Git;

namespace SelfContainedDeployment
{
    internal sealed class InspectorDirectoryDecoration
    {
        public GitChangedFile File { get; init; }

        public bool HasChangedDescendant { get; init; }
    }

    internal sealed class InspectorDirectoryNodeModel
    {
        public string Name { get; init; }

        public string RelativePath { get; init; }

        public bool IsDirectory { get; init; }

        public InspectorDirectoryDecoration Decoration { get; init; }

        public Dictionary<string, InspectorDirectoryNodeModel> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class InspectorDirectoryBuildResult
    {
        public string RootPath { get; init; }

        public string RenderKey { get; init; }

        public int FileCount { get; init; }

        public List<InspectorDirectoryNodeModel> RootNodes { get; init; } = new();
    }

    internal sealed class InspectorDirectoryUiCache
    {
        public string RootPath { get; init; }

        public string RenderKey { get; init; }

        public int FileCount { get; init; }

        public List<InspectorDirectoryNodeModel> RootNodes { get; init; } = new();

        public Dictionary<string, InspectorDirectoryNodeModel> ModelsByPath { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
