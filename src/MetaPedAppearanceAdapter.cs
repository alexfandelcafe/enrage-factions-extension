using System;
using RDR2;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Thin adapter around the RDR2 MetaPed natives used by YMT outfit assets. Native hashes and
    /// the argument layout are verified against public RDR2 native references; no trainer menus
    /// or trainer runtime are copied into this extension.
    /// </summary>
    internal static class MetaPedAppearanceAdapter
    {
        private const ulong SetMetaPedTagHash = 0xBC6DF00D7A4A6819UL;
        private const ulong RemoveTagFromMetaPedHash = 0xD710A5007C2AC539UL;
        private const ulong MakeMetaPedVisibleHash = 0x704C908E9C405136UL;
        private const ulong UpdatePedVariationHash = 0xCC8CA3E88256E58FUL;

        internal static bool ApplyComponent(Ped ped, MemberAppearanceComponent component, out string error)
        {
            error = null;
            if (ped == null || !ped.Exists() || ped.IsDead)
            {
                error = "El NPC ya no está disponible en el mundo.";
                return false;
            }
            if (component == null || component.DrawableHash == 0U || component.CategoryTag == 0U)
            {
                error = "El componente no contiene un drawable o una categoría válida.";
                return false;
            }

            try
            {
                Function.Call(RemoveTagFromMetaPedHash, new InputArgument[]
                {
                    ped.Handle, unchecked((int)component.CategoryTag), 1
                });
                Function.Call(SetMetaPedTagHash, new InputArgument[]
                {
                    ped.Handle,
                    unchecked((int)component.DrawableHash),
                    unchecked((int)component.AlbedoHash),
                    unchecked((int)component.NormalHash),
                    unchecked((int)component.MaterialHash),
                    unchecked((int)component.PaletteHash),
                    component.Tint0,
                    component.Tint1,
                    component.Tint2
                });
                Refresh(ped);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("MetaPed component application failed for " + component.CategoryKey + ": " + ex);
                return false;
            }
        }

        internal static bool ApplyProfile(Ped ped, MemberAppearanceProfile profile, out string error)
        {
            error = null;
            if (ped == null || !ped.Exists() || ped.IsDead)
            {
                error = "El NPC ya no está disponible en el mundo.";
                return false;
            }
            if (profile == null || profile.Components == null || profile.Components.Count == 0)
                return true;

            try
            {
                foreach (MemberAppearanceComponent component in profile.Components)
                {
                    if (component == null || component.CategoryTag == 0U || component.DrawableHash == 0U)
                        continue;
                    Function.Call(RemoveTagFromMetaPedHash, new InputArgument[]
                    {
                        ped.Handle, unchecked((int)component.CategoryTag), 1
                    });
                    Function.Call(SetMetaPedTagHash, new InputArgument[]
                    {
                        ped.Handle,
                        unchecked((int)component.DrawableHash),
                        unchecked((int)component.AlbedoHash),
                        unchecked((int)component.NormalHash),
                        unchecked((int)component.MaterialHash),
                        unchecked((int)component.PaletteHash),
                        component.Tint0,
                        component.Tint1,
                        component.Tint2
                    });
                }

                Refresh(ped);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("MetaPed profile reapplication failed for " + profile.MemberId + ": " + ex);
                return false;
            }
        }

        private static void Refresh(Ped ped)
        {
            Function.Call(MakeMetaPedVisibleHash, new InputArgument[] { ped.Handle });
            // RDR2 requires this variation update after MetaPed tags are changed.
            Function.Call(UpdatePedVariationHash, new InputArgument[]
            {
                ped.Handle, false, true, true, true, false
            });
        }
    }
}
