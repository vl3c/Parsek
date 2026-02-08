using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ParsekToolbarRegistration : MonoBehaviour
    {
        void Start()
        {
            ToolbarControl.RegisterMod(ParsekFlight.MODID, ParsekFlight.MODNAME);
        }
    }
}
