using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the order in which KSP creates Parsek's [KSPAddon] MonoBehaviours.
    /// Stock AddonLoader.StartAddons walks each loaded assembly's GetTypes() and
    /// adds one component per matching [KSPAddon] type in that order, so the order
    /// below is the order of Awake / OnEnable / Start, of Update / LateUpdate / OnGUI
    /// for addons created in the same pass, and of every GameEvents subscription made
    /// in those Awakes. GetTypes() returns TypeDef row order, which the compiler
    /// derives from source file order, so a file or folder rename can reorder it
    /// without touching a line of code. The read stays reflection-only
    /// (GetCustomAttributesData, no attribute instance, no Unity call).
    /// </summary>
    public class KspAddonCreationOrderTests
    {
        private static readonly string[] Expected =
        {
            "Instantly once Parsek.NoSaveSceneExitAddon",
            "Flight Parsek.ParsekFlight",
            "Instantly once Parsek.ParsekHarmony",
            "SpaceCentre Parsek.ParsekKSC",
            "TrackingStation Parsek.ParsekTrackingStation",
            // The predicted-crash reconciler hosts subscribe to no GameEvents and act only
            // seconds after FlightGlobals is ready, so their place after the scene controllers
            // cannot matter (they read ParsekFlight.Instance in Update, after its Awake).
            "Flight Parsek.PredictedCrashReconcilerFlight",
            "TrackingStation Parsek.PredictedCrashReconcilerTrackingStation",
            "Instantly once Parsek.SaveFileLoadIntentAddon",
            "Instantly once Parsek.WarpToTimeConsumer",
            "EveryScene Parsek.GuiTreeRecorderPump",
            "Instantly once Parsek.MapRenderProbe",
            "Instantly once Parsek.PostFlightDialogLogHost",
            "PSystemSpawn once Parsek.StockWarpAltitudeLimitsCapture",
            "EveryScene Parsek.CurrencyReservationOverlay",
            "Instantly once Parsek.ParsekToolbarRegistration",
            "SpaceCentre Parsek.StockUiOverlayController",
            "Instantly once Parsek.TestCommands.ParsekTestCommandAddon",
            "Editor Parsek.Patches.EditorLaunchMark",
            "Instantly once Parsek.MapRender.RenderCompositionRecorder",
            "Instantly once Parsek.InGameTests.TestRunnerShortcut",
            "Instantly once Parsek.Display.GhostTrajectoryPolylineRenderer+Driver",
        };

        internal static string StartupName(int startup)
        {
            switch (startup)
            {
                case -2: return "Instantly";
                case -1: return "EveryScene";
                case 2: return "MainMenu";
                case 5: return "SpaceCentre";
                case 6: return "Editor";
                case 7: return "Flight";
                case 8: return "TrackingStation";
                case 9: return "PSystemSpawn";
                default: return startup.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        internal static List<string> ReadAddonCreationOrder(Assembly assembly)
        {
            var rows = new List<string>();
            foreach (Type type in assembly.GetTypes())
            {
                foreach (CustomAttributeData data in type.GetCustomAttributesData())
                {
                    if (data.AttributeType.FullName != "KSPAddon")
                        continue;
                    IList<CustomAttributeTypedArgument> args = data.ConstructorArguments;
                    int startup = Convert.ToInt32(args[0].Value, System.Globalization.CultureInfo.InvariantCulture);
                    bool once = args.Count > 1 && args[1].Value is bool b && b;
                    rows.Add(StartupName(startup) + (once ? " once " : " ") + type.FullName);
                }
            }
            return rows;
        }

        [Fact]
        public void KspAddonCreationOrder_IsTheMeasuredOrder()
        {
            List<string> actual = ReadAddonCreationOrder(typeof(ParsekHarmony).Assembly);

            string message =
                "The [KSPAddon] types of Parsek.dll no longer come out of GetTypes() in the pinned order. " +
                "KSP's AddonLoader creates addons in exactly this order (TypeDef row order), so it decides " +
                "which Awake / Start / Update / OnGUI runs first in a pass and the order of GameEvents " +
                "subscriptions made in Awake. A file or folder rename, or a new file, can change it with no " +
                "code change. Re-derive the per-scene pairs whose order moved, decide for each whether it can " +
                "matter (see the ARCH-MODULE-FOLDERS entry in docs/dev/todo-and-known-bugs.md), and only then " +
                "re-pin. Actual order:\n  " + string.Join("\n  ", actual);

            Assert.True(Expected.SequenceEqual(actual), message);
        }

        [Fact]
        public void StartupName_MapsEveryStockValueParsekUses()
        {
            Assert.Equal("Instantly", StartupName(-2));
            Assert.Equal("EveryScene", StartupName(-1));
            Assert.Equal("Editor", StartupName(6));
            Assert.Equal("PSystemSpawn", StartupName(9));
            Assert.Equal("42", StartupName(42));
        }
    }
}
