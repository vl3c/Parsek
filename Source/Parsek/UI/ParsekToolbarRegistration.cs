using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ParsekToolbarRegistration : MonoBehaviour
    {
        private static bool registered;

        void Start()
        {
            if (registered)
            {
                ParsekLog.Warn("Toolbar", "Toolbar registration skipped: already registered");
                return;
            }

            try
            {
                ToolbarControl.RegisterMod(ParsekFlight.MODID, ParsekFlight.MODNAME);
                registered = true;
                ParsekLog.Info("Toolbar",
                    $"Toolbar registered: modId={ParsekFlight.MODID}, modName={ParsekFlight.MODNAME}");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Toolbar", $"Toolbar registration failed: {ex.Message}");
            }
        }
    }
}
