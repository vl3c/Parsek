namespace Parsek
{
    /// <summary>
    /// Lets a caller of the timeline ghost build change how a snapshot part is built
    /// without the ghost code knowing who the caller is. The build asks the dresser once
    /// per snapshot PART, before the part lookup.
    /// </summary>
    internal interface IGhostPartDresser
    {
        /// <summary>
        /// The part name to build in place of the recorded one, or null / empty to keep
        /// the recorded name. Receives the name after the snapshot suffix is stripped.
        /// </summary>
        string SubstitutePartName(string recordedPartName);
    }

    /// <summary>
    /// Per-build options for <see cref="GhostVisualBuilder.BuildTimelineGhostFromSnapshot"/>
    /// and <see cref="GhostVisualBuilder.TryBeginTimelineGhostBuild"/>. A null options
    /// object, or one with a null dresser, builds exactly what the snapshot names.
    /// </summary>
    internal sealed class GhostBuildOptions
    {
        internal readonly IGhostPartDresser Dresser;

        internal GhostBuildOptions(IGhostPartDresser dresser)
        {
            Dresser = dresser;
        }
    }
}
