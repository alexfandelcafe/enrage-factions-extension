using System;
using System.Reflection;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>Independent camp NPC and compact mission board; does not reuse the administrator.</summary>
    internal sealed class MissionGiverRuntime
    {
        private const ulong IsControlJustPressedHash = 0x580417101DDB492FUL;
        private const ulong PromptGetGroupForEntityHash = 0xB796970BD125FCE8UL;
        private const int ContextControl = 1980406895;
        private const int Up = unchecked((int)0x6319DB71);
        private const int Down = unchecked((int)0x05CA7C52);
        private const int Accept = unchecked((int)0xC7B5340A);
        private const int Cancel = unchecked((int)0x156F7119);
        private readonly PlayerFactionData _faction;
        private readonly EnRAGEBridge _bridge;
        private readonly MissionManager _missions;
        private readonly Action<string> _notify;
        private Ped _ped;
        private int _nextSpawnAt;
        private int _nextProtectAt;
        private int _prompt;
        private int _promptGroup;
        private bool _promptArmed = true;
        private bool _menuOpen;
        private int _index;
        private Type _uiType;
        private MethodInfo _rect;
        private MethodInfo _text;
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal MissionGiverRuntime(PlayerFactionData faction, EnRAGEBridge bridge, MissionManager missions, Action<string> notify)
        {
            _faction = faction; _bridge = bridge; _missions = missions; _notify = notify;
        }

        internal void Tick(Ped player)
        {
            if (!_faction.Founded || string.IsNullOrWhiteSpace(_faction.CampSiteId) || !_bridge.IsAvailable || player == null || !player.Exists())
            { Cleanup(); return; }
            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            float campDistance = player.Position.DistanceTo(centre);
            if (campDistance > 180f) { Cleanup(); return; }
            if ((_ped == null || !_ped.Exists()) && campDistance <= 85f && Game.GameTime >= _nextSpawnAt)
            {
                _nextSpawnAt = Game.GameTime + 3500;
                Spawn(centre);
            }
            if (_ped == null || !_ped.Exists()) return;
            if (Game.GameTime >= _nextProtectAt)
            {
                _nextProtectAt = Game.GameTime + 750;
                try
                {
                    ENTITY.SET_ENTITY_INVINCIBLE(_ped.Handle, true);
                    _ped.BlockPermanentEvents = true;
                    _ped.IsPersistent = true;
                }
                catch (Exception ex) { Log.Write("Mission giver protection warning: " + ex.Message); }
            }
            if (_menuOpen) { UpdateMenu(); return; }
            if (player.Position.DistanceTo(_ped.Position) > 2.8f || player.IsOnMount || player.IsInVehicle)
            { SetPromptVisible(false); _promptArmed = true; return; }
            int group;
            try { group = Function.Call<int>(PromptGetGroupForEntityHash, new InputArgument[] { _ped.Handle }); }
            catch { group = 0; }
            if (_prompt == 0 || group != _promptGroup || !PromptValid(_prompt))
            {
                RemovePrompt();
                if (group != 0) TryCreatePrompt(group);
            }
            SetPromptVisible(true);
            bool pressed = JustPressed(ContextControl);
            bool completed = PromptCompleted(_prompt);
            if (!pressed && !completed) { _promptArmed = true; return; }
            if (!_promptArmed && !pressed) return;
            _promptArmed = false;
            SetPromptVisible(false);
            _menuOpen = true; _index = 0;
            _missions.RefreshAvailability();
            Log.Write("Mission giver menu opened.");
        }

        private void Spawn(Vector3 centre)
        {
            try
            {
                Vector3 pos = new Vector3(centre.X - 1.8f, centre.Y + 1.0f, centre.Z + 0.2f);
                STREAMING.REQUEST_COLLISION_AT_COORD(pos.X, pos.Y, pos.Z);
                Ped ped = _bridge.SpawnFounder("U_M_M_RhdBartender_01", pos, 180f);
                if (ped == null || !ped.Exists()) { Log.Write("Mission giver spawn failed."); return; }
                _ped = ped; _bridge.MarkPedAsEnRageOwned(ped);
                try { ENTITY.SET_ENTITY_INVINCIBLE(ped.Handle, true); } catch { }
                Log.Write("Independent mission giver spawned: " + ped.Handle + ".");
            }
            catch (Exception ex) { Log.Write("Mission giver spawn exception: " + ex); }
        }

        private void UpdateMenu()
        {
            DrawMenu();
            if (JustPressed(Cancel)) { _menuOpen = false; _promptArmed = true; return; }
            int count = _missions.Definitions.Count + 1;
            if (JustPressed(Up)) { _index = (_index + count - 1) % count; return; }
            if (JustPressed(Down)) { _index = (_index + 1) % count; return; }
            if (!JustPressed(Accept)) return;
            if (_index == count - 1) { _menuOpen = false; _promptArmed = true; return; }
            MissionDefinition def = _missions.Definitions[_index];
            MissionRecord rec = FindRecord(def.Id);
            if (rec != null && rec.Completed && !rec.RewardClaimed)
            {
                _missions.Claim(def.Id);
                return;
            }
            if (rec != null && rec.Accepted)
            {
                if (_notify != null) _notify(def.Title + " · progreso " + rec.Progress + "/" + def.RequiredEvents + ".");
                return;
            }
            if (!_missions.CanAccept(def.Id))
            {
                if (_notify != null) _notify("Este encargo no está disponible o ya no puede iniciarse.");
                return;
            }
            _missions.Accept(def.Id);
        }

        private MissionRecord FindRecord(string id)
        {
            foreach (MissionRecord r in _missions.Records)
                if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        private void DrawMenu()
        {
            if (!ResolveUi()) return;
            Rect(0.16f, 0.12f, 0.68f, 0.76f, 14, 13, 12, 244);
            Rect(0.16f, 0.12f, 0.68f, 0.004f, 187, 145, 82, 255);
            Text("ENCARGOS DE LA BANDA", 0.19f, 0.145f, 0.30f, 245, 235, 214, 255);
            Text((_faction.Name ?? "La Banda") + " · PRESTIGIO " + _faction.Prestige, 0.19f, 0.19f, 0.18f, 190, 174, 145, 255);
            int i = 0;
            foreach (MissionDefinition def in _missions.Definitions)
            {
                float y = 0.235f + i * 0.073f;
                MissionRecord rec = FindRecord(def.Id);
                bool selected = i == _index;
                string status = rec != null && rec.Completed && !rec.RewardClaimed ? " · COBRAR" :
                    rec != null && rec.Accepted ? " · ACTIVA " + rec.Progress + "/" + def.RequiredEvents :
                    rec != null && rec.RewardClaimed ? " · COMPLETADA" :
                    _missions.CanAccept(def.Id) ? " · DISPONIBLE" : " · NO DISPONIBLE";
                Rect(0.19f, y, 0.62f, 0.059f, selected ? 63 : 30, selected ? 48 : 28, selected ? 29 : 25, selected ? 245 : 220);
                Text((selected ? "> " : "  ") + def.Title + status, 0.205f, y + 0.006f, 0.17f, selected ? 250 : 205, selected ? 235 : 199, selected ? 208 : 187, 255);
                Text(def.Description, 0.22f, y + 0.029f, 0.115f, 185, 178, 162, 255);
                i++;
            }
            float closeY = 0.235f + i * 0.073f;
            Rect(0.19f, closeY, 0.62f, 0.05f, _index == i ? 63 : 30, _index == i ? 48 : 28, _index == i ? 29 : 25, 235);
            Text((_index == i ? "> " : "  ") + "Cerrar", 0.205f, closeY + 0.008f, 0.17f, 230, 220, 200, 255);
            Text("Arriba/abajo: elegir · Aceptar: aceptar/cobrar · Atrás: cerrar", 0.20f, 0.83f, 0.13f, 180, 170, 150, 255);
        }

        private bool ResolveUi()
        {
            if (_rect != null && _text != null) return true;
            try
            {
                Assembly a = _bridge.MainInstance != null ? _bridge.MainInstance.GetType().Assembly : AppDomain.CurrentDomain.GetAssemblies()[0];
                _uiType = a.GetType("NemesisSystem.UiDraw", false);
                if (_uiType == null) return false;
                _rect = Array.Find(_uiType.GetMethods(Flags), m => m.Name == "Rect" && m.GetParameters().Length == 8);
                _text = Array.Find(_uiType.GetMethods(Flags), m => m.Name == "Text" && m.GetParameters().Length == 8);
                return _rect != null && _text != null;
            }
            catch { return false; }
        }
        private void Rect(float x,float y,float w,float h,int r,int g,int b,int a) { try { _rect.Invoke(null,new object[]{x,y,w,h,r,g,b,a}); } catch { } }
        private void Text(string t,float x,float y,float scale,int r,int g,int b,int a) { try { _text.Invoke(null,new object[]{t,x,y,scale,r,g,b,a}); } catch { } }
        private bool JustPressed(int control)
        {
            try { return Function.Call<bool>(IsControlJustPressedHash, new InputArgument[]{0,control}) || Function.Call<bool>(IsControlJustPressedHash,new InputArgument[]{2,control}); }
            catch { return false; }
        }
        private bool TryCreatePrompt(int group)
        {
            try
            {
                Type type = _bridge.MainInstance.GetType();
                MethodInfo make = Array.Find(type.GetMethods(Flags),m=>m.Name=="MakePrompt"&&m.GetParameters().Length==4);
                if (make == null) return false;
                Type ct=make.GetParameters()[0].ParameterType;
                object control=ct.IsEnum?Enum.ToObject(ct,ContextControl):(object)ContextControl;
                _prompt=Convert.ToInt32(make.Invoke(null,new object[]{control,"Consultar encargos",true,group}));
                _promptGroup=group; return _prompt!=0;
            }
            catch(Exception ex){Log.Write("Mission giver prompt creation warning: "+ex.Message);return false;}
        }
        private bool PromptValid(int p)
        {
            try { Type t=_bridge.MainInstance.GetType(); MethodInfo m=Array.Find(t.GetMethods(Flags),x=>x.Name=="PromptExists"&&x.GetParameters().Length==1); return m==null||Convert.ToBoolean(m.Invoke(null,new object[]{p})); }
            catch { return false; }
        }
        private bool PromptCompleted(int p)
        {
            try
            {
                Type hud=typeof(Game).Assembly.GetType("RDR2.Native.HUD",false);
                MethodInfo m=hud==null?null:Array.Find(hud.GetMethods(Flags),x=>x.Name=="_UI_PROMPT_HAS_HOLD_MODE_COMPLETED"&&x.GetParameters().Length==1);
                return m!=null&&Convert.ToBoolean(m.Invoke(null,new object[]{p}));
            }
            catch{return false;}
        }
        private void SetPromptVisible(bool visible)
        {
            if (_prompt==0) return;
            try { Type hud=typeof(Game).Assembly.GetType("RDR2.Native.HUD",false); MethodInfo m=hud==null?null:Array.Find(hud.GetMethods(Flags),x=>x.Name=="_UI_PROMPT_SET_VISIBLE"&&x.GetParameters().Length==2); if(m!=null)m.Invoke(null,new object[]{_prompt,visible}); } catch { }
        }
        private void RemovePrompt()
        {
            if (_prompt!=0) try { Type hud=typeof(Game).Assembly.GetType("RDR2.Native.HUD",false); MethodInfo m=hud==null?null:Array.Find(hud.GetMethods(Flags),x=>x.Name=="_UI_PROMPT_DELETE"&&x.GetParameters().Length==1); if(m!=null)m.Invoke(null,new object[]{_prompt}); } catch { }
            _prompt=0;_promptGroup=0;_promptArmed=true;
        }
        internal void Cleanup()
        {
            _menuOpen=false; SetPromptVisible(false); RemovePrompt();
            try { if(_ped!=null&&_ped.Exists())_ped.Delete(); } catch { }
            _ped=null;
        }
    }
}
