using System;
using RDR2;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// ScriptHookRDR2DotNet V2 entry point. enRAGE is loaded independently; the runtime
    /// waits for NemesisSystem.NemesisMod and then installs a Reflection/Harmony bridge.
    /// </summary>
    public sealed class EntryPoint : Script
    {
        private readonly PlayerFactionRuntime _runtime;

        public EntryPoint()
        {
            _runtime = new PlayerFactionRuntime();
            Tick += OnTick;
            Aborted += OnAborted;
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                _runtime.Tick();
            }
            catch (Exception ex)
            {
                Log.Write("ERROR tick: " + ex);
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try { _runtime.OnScriptAborted(); }
            catch (Exception ex) { Log.Write("ERROR script-aborted cleanup: " + ex); }
        }
    }
}
