using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// The Structure List window's whole mocked target: the step rows plus the two
    /// fields that decide its title and its empty-state wording.
    /// </summary>
    internal sealed class GuiMockStructure
    {
        /// <summary>True for <c>TargetMode.Route</c>, false for
        /// <c>TargetMode.Mission</c>. It is not cosmetic: the empty-list message differs
        /// between the two, and the route mode is the one that draws Origin / Deliver /
        /// Pick up rows.</summary>
        internal bool RouteMode;

        /// <summary>The window title's tail (the window draws "Parsek - " + this).</summary>
        internal string Title;

        /// <summary>The rows, already in the order the builder would have produced.</summary>
        internal List<StructureStep> Steps;
    }

    /// <summary>
    /// The union one applier arm hands to one window.
    ///
    /// <para><b>ONLY THE WINDOWS THIS BUILD SUPPORTS HAVE A FIELD.</b> The design sketched
    /// the full eleven-window union up front; carrying nine null fields no code reads
    /// would be dead surface that reads as coverage, and the whole point of a C# catalogue
    /// is that an unbuildable state is a compile error. A later phase adds its window's
    /// field in the same commit as its builder, its applier arm and its refusal row.</para>
    /// </summary>
    internal sealed class GuiMockPayload
    {
        /// <summary>Kerbals: a whole built view model (both tabs).</summary>
        internal KerbalsWindowUI.KerbalsViewModel? Kerbals;

        /// <summary>Career State: a whole built view model (all four tabs plus the
        /// banner).</summary>
        internal CareerStateWindowUI.CareerStateViewModel? Career;

        /// <summary>Structure List: steps plus target mode and title.</summary>
        internal GuiMockStructure Structure;

        /// <summary>The window token this payload is FOR, checked against the state's own
        /// <c>Window</c> by a unit cell so a builder filed under the wrong window is a
        /// local red rather than a <c>mock-not-applied</c> after a boot.</summary>
        internal string Window;
    }
}
