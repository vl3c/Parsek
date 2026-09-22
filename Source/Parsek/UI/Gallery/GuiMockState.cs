using System;
using Parsek.TestCommands;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// One catalogue entry: a synthetic view model for ONE window, declared in C# beside
    /// the presentation types it constructs.
    ///
    /// <para><b>WHY C# AND NOT A DATA FILE.</b> Every builder constructs the REAL model
    /// types, so a renamed field, a changed struct shape or a new required member is a
    /// compile error in the same build that changed the window. A TOML or JSON catalogue
    /// would carry the rename silently and the gallery would photograph a stale state
    /// while claiming coverage.</para>
    ///
    /// <para><b>AND WHY A BUILDER NEVER TYPES A RENDERED STRING.</b> The point of a mocked
    /// capture is to show the owner a picture the product can actually produce. A builder
    /// therefore constructs the INPUTS and lets the real pure presentation helper render
    /// the cell - <c>KerbalsPresentation.BuildRosterRows</c>, <c>CareerStateWindowUI.Build</c>,
    /// <c>MissionComposition.TerminalName</c> - exactly as the game does. A state that
    /// typed its own cell text would teach the owner about a string no code path emits.</para>
    /// </summary>
    internal sealed class GuiMockState
    {
        /// <summary>The stable key: <c>&lt;window&gt;.&lt;family&gt;.&lt;variant&gt;</c>,
        /// ASCII lower case, dots and dashes only. It becomes the capture label's tail
        /// and the mirror's state token, so it is renamed only deliberately.</summary>
        internal string Id;

        /// <summary>A <c>TestCommandUiAction</c> window token.</summary>
        internal string Window;

        /// <summary>The tab token this state pins, or null for a window with no
        /// selector. Pinned IN THE STATE rather than left to the lane because the
        /// draw-produced read-back (<c>mock-not-applied</c>) looks for cells only the
        /// right tab draws: a roster witness under the Flights tab would answer
        /// not-applied over a perfectly good mock.</summary>
        internal string Tab;

        /// <summary>Roster / group keys this state needs EXPANDED for its rows to draw
        /// (chain views, fold buckets). Empty for most states. Driven through the
        /// window's own expand setter and restored with the session.</summary>
        internal string[] ExpandKeys = new string[0];

        /// <summary>The scene this state requires, or null when any scene that draws the
        /// window will do. All three P1 windows draw in both, so every P1 state is
        /// null.</summary>
        internal TestCommandScene? Scene;

        /// <summary>The size the gallery gives the window before capturing, so one shot
        /// shows the rows without scrolling (design D2: every window already has
        /// <c>WindowRectForTesting</c>). Zero means "leave the window as it is".</summary>
        internal int RectW;

        /// <summary>See <see cref="RectW"/>.</summary>
        internal int RectH;

        /// <summary>Builds the synthetic model. Called ONCE per apply, never cached: a
        /// builder that returned a shared mutable list would let one capture's expansion
        /// state leak into the next.</summary>
        internal Func<GuiMockPayload> Build;

        /// <summary>The branch keys this state claims, as
        /// <c>&lt;EnumName&gt;.&lt;Member&gt;</c>. Read by the completeness guard
        /// (design section 11), which reflects the enums the supported windows' draw
        /// branches switch on and names any member no state claims.</summary>
        internal string[] Covers = new string[0];

        /// <summary>One line: why this state exists, and any LIVE cell it cannot pin.
        /// Goes onto the apply log line, so a reader of <c>KSP.log</c> can tell a
        /// deliberately half-live capture from a defect.</summary>
        internal string Note;
    }
}
