#region using
using System;
using System.Collections.Generic;
using System.Threading;
using Bots.Gatherbuddy;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.Inventory;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.Logic.Profiles;
using Styx.Plugins.PluginClass;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
#endregion

namespace Styx.Bot.Plugins.MobileBanker
{
    class BankSlotInfo
    {
        public BankSlotInfo(int bag, int bagType, int slot, uint itemID, uint stackSize, int maxStackSize)
        {
            Bag = bag;
            BagType = bagType;
            Slot = slot;
            ItemID = itemID;
            StackSize = stackSize;
            MaxStackSize = maxStackSize;
        }
        public int Bag { get; private set; } // this is also the tab for GBank
        public int BagType { get; private set; }
        public int Slot { get; private set; }
        public uint ItemID { get; set; } //  0 if slot is has no items.
        public uint StackSize { get; set; } // amount of items in slot..
        public int MaxStackSize { get; set; }
    };

    struct FreeSlots
    {
        public int tab;
        public int free_slots;
    };

    public class MobileBanker : HBPlugin
    {
        #region Settings and overrides
        public override string Name { get { return "MobileBanker"; } }
        public override string Author { get { return "Laniax"; } }
        public override Version Version { get { return new Version(0, 9, 9); } }
        
        private static bool _hasGuildChest = false;
        private static int guildChest = 83958; // spellID of Mobile Banking
        private static readonly LocalPlayer _me = StyxWoW.Me;
        private static bool _isBanking = false;
        private static UInt32 numTabs;

        const string GbankSlotInfo =
             "local _,c,l=GetGuildBankItemInfo({0}, {1}) " +
             "if c > 0 and l == nil then " +
                 "local id = tonumber(string.match(GetGuildBankItemLink({0},{1}), 'Hitem:(%d+)')) " +
                 "local maxStack = select(8,GetItemInfo(id)) " +
                 "return id,c,maxStack " +
             "elseif c == 0 then " +
                "return 0,0,0 " +
             "end ";
        #endregion

        #region Initialization, searching for Mobile Banking
        public override void Initialize()
        {
            Log("MobileBanker v"+Version.ToString()+" initializing.");
            using (new FrameLock())
            {
                //check if we have the remote GBank spell
                if (_me.GuildLevel >= 11)
                {
                    Log("Guildlevel 11+, using Mobile Banking for guildbank purposes.");
                    _hasGuildChest = true;
                }
                else
                    Log("Your guild is not level 11 yet, MobileBanker will do nothing.");
            }
        }
        #endregion

        #region Pulse
        public override void Pulse()
        {
            if (Styx.BotManager.Current.Name == "Gatherbuddy2")
            {
                if (Logic.BehaviorTree.TreeRoot.StatusText.Contains("Guild Vault") && GatherbuddySettings.Instance.UseGuildVault)
                {
                    Log("Detected a force empty bags command. overriding with MobileBanker.");
                    BotPoi.Clear();
                    Logic.Combat.SpellManager.StopCasting();
                    Thread.Sleep(300);
                    Bank();
                }
                else
                {
                    if (!_isBanking)
                    {
                        var _gb = GatherbuddySettings.Instance;
                        if (_me.FreeNormalBagSlots < 5 && _gb.UseGuildVault)
                            Bank();
                    }
                }
            }
        }
        #endregion

        #region Banking
        public static void Bank()
        {
            if (_me.IsGhost || _me.Dead || _isBanking || !_hasGuildChest)
                return;

            _isBanking = true;

            if (Styx.Logic.Combat.SpellManager.Spells["Mobile Banking"].CooldownTimeLeft.TotalSeconds == 0)
            {
                if (_me.IsFlying)
                {
                    var landingSpot = findSafeFlightPoint(_me.Location);
                    while (landingSpot.Distance(_me.Location) > 1)
                    {
                        Flightor.MoveTo(landingSpot);
                        Thread.Sleep(200);
                    }
                    if (_me.Shapeshift == ShapeshiftForm.FlightForm)
                        Styx.Logic.Combat.SpellManager.Cast("Flight Form");
                    else
                        Mount.Dismount();
                }

                Log("Guild Chest available and off cooldown, using it now.");
                Styx.Logic.Combat.SpellManager.Cast(83958); // guild chest
                Thread.Sleep(4000);
                ObjectManager.Update();
                List<WoWGameObject> _unitList = ObjectManager.GetObjectsOfType<WoWGameObject>();
                foreach (WoWGameObject u in _unitList)
                {
                    if (u.SubType == WoWGameObjectType.GuildBank)
                    {
                        u.Interact();
                        Log("Interacting with chest.");
                        Thread.Sleep(2500);
                        numTabs = UInt32.Parse(Lua.GetReturnValues("return GetNumGuildBankTabs()")[0]);

                        if (numTabs > 0)
                        {
                            for (int currentTab = 1; currentTab < numTabs; currentTab++)
                            {
                                FreeSlots slots = new FreeSlots();

                                Log("Switching to guildbank tab: " + currentTab);
                                Lua.DoString("SetCurrentGuildBankTab(" + currentTab + ")");
                                int free_slots_in_this_tab = 0;

                                List<WoWItem> itemsToDeposit = new List<WoWItem>();
                                using (new FrameLock())
                                {
                                    List<BankSlotInfo> _slots = GetBankTabInfo(currentTab);
                                    for (int i = 0; i < _slots.Count; i++)
                                    {
                                        if (_slots[i].StackSize == 0)
                                            free_slots_in_this_tab++;
                                    }
                                    
                                    slots.tab = currentTab;
                                    slots.free_slots = free_slots_in_this_tab;

                                    Log("There are " + slots.free_slots.ToString() + " slots free in current tab.");

                                    itemsToDeposit = _me.BagItems.FindAll(i => !ProfileManager.CurrentProfile.ProtectedItems.HashSet1.Contains(i.Entry) && !i.IsSoulbound);
                                    WoWItem food = Consumable.GetBestFood(false);
                                    if (food != null)
                                    {
                                        itemsToDeposit.Remove(food);
                                    }

                                    WoWItem drink = Consumable.GetBestDrink(false);
                                    if (drink != null)
                                    {
                                        itemsToDeposit.Remove(drink);
                                    }
                                }
                                for (int i = 0; i < itemsToDeposit.Count; i++)
                                {
                                    if (slots.free_slots > 0)
                                    {
                                        itemsToDeposit[i].UseContainerItem();
                                        --slots.free_slots;
                                        Thread.Sleep(1000);
                                    }
                                }
                            }
                            BotPoi.Clear();
                            Log("Finished banking.");
                        }
                        else
                            Log("FATAL! YOUR GUILDBANK DOESN'T HAVE ANY TABS!");

                        break; // breaks objectmgr
                    }
                }
                _isBanking = false;
            }
        }
        
        const int GuildTabSlotNum = 98;
        static List<BankSlotInfo> GetBankTabInfo(int tab)
        {
            List<BankSlotInfo> bankSlotInfo = new List<BankSlotInfo>();
            using (new FrameLock())
            {
                // check permissions for tab
                bool canDespositInTab =
                    Lua.GetReturnVal<int>(string.Format("local _,_,v,d =GetGuildBankTabInfo({0}) if v==1 and d==1 then return 1 else return 0 end", tab), 0) == 1;
                if (canDespositInTab)
                {
                    for (int slot = 1; slot <= GuildTabSlotNum; slot++)
                    {
                        // 3 return values in following order, ItemID,StackSize,MaxStackSize
                        string lua = string.Format(GbankSlotInfo, tab, slot);
                        List<string> retVals = Lua.GetReturnValues(lua);
                        bankSlotInfo.Add(new BankSlotInfo(tab, 0, slot, uint.Parse(retVals[0]), uint.Parse(retVals[1]), int.Parse(retVals[2])));
                    }
                }
                return bankSlotInfo;
            }
        }
        #endregion

        #region Method for finding a safe landing point
        private static WoWPoint findSafeFlightPoint(WoWPoint loc)
        {
            #region If multiple layers (heights), attempt to land somewhere nearby
            var _heights = Logic.Pathing.Navigator.FindHeights(loc.X, loc.Y);
            _heights.Sort();
            if (_heights.Count > 1)
            {
                Random rand = new Random();
                var i = 1;
                var _newSpot = new WoWPoint(0, 0, 0);
                while (i < 100)
                {
                    _newSpot = new WoWPoint((loc.X + rand.Next(-i, i)), (loc.Y + rand.Next(-i, i)), 0);
                    while (Logic.Pathing.Navigator.FindHeights(_newSpot.X, _newSpot.Y).Count > 1)
                    {
                        _newSpot = new WoWPoint((loc.X + rand.Next(-i, i)), (loc.Y + rand.Next(-i, i)), 0);
                        i = i + 1;
                    }
                    Logic.Pathing.Navigator.FindHeight(_newSpot.X, _newSpot.Y, out _newSpot.Z);
                    if (Navigator.CanNavigateFully(_newSpot, loc) && clearSpot(_newSpot))
                    {
                        Log("Took {0} tries to find a safe(?) spot!", i);
                        Log("Landing spot: {0}", _newSpot.ToString());
                        return _newSpot;
                    }
                }
                Log("No safe spot found :(");
            }
            return loc;
        }
        #endregion
        #endregion

        #region Checking a possible landing spot for clearance
        public static bool clearSpot(WoWPoint loc)
        {
            for (double i = -5.0; i <= 5.0; i = i + 0.5)
            {
                for (double j = -5.0; j <= 5.0; j = j + 0.5)
                {
                    var _tempLoc = new WoWPoint(loc.X + i, loc.Y + j, loc.Z);
                    if (Logic.Pathing.Navigator.FindHeights(_tempLoc.X, _tempLoc.Y).Count != 1 ||
                        !WoWInternals.World.GameWorld.IsInLineOfSight(new WoWPoint(_tempLoc.X, _tempLoc.Y, _tempLoc.Z + 50), _tempLoc))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        #endregion
        
        #region Logging methods
        private static void Log(bool debug, string format, params object [] args)
        {
            if (debug)
                Logging.WriteDebug("[MobileBanker] {0}", string.Format(format, args));
            else
                Logging.Write("[MobileBanker] {0}", string.Format(format, args));
        }
        
        private static void Log(string format)
        {
            Log(false, format);
        }
        
        private static void Log(string format, params object [] args)
        {
            Log(false, format, args);
        }
        #endregion
    }
}