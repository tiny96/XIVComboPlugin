using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Structs.JobGauge;
using Dalamud.Hooking;
using XIVComboPlugin.JobActions;
using Serilog;
using System.Threading.Tasks;
using System.Threading;
using Dalamud.Plugin;
using System.Dynamic;

namespace XIVComboPlugin
{
    public class IconReplacer
    {
        public delegate ulong OnCheckIsIconReplaceableDelegate(uint actionID);

        public delegate ulong OnGetIconDelegate(byte param1, uint param2);

        public delegate ulong OnRequestActionDetour(long param_1, uint param_2, ulong param_3, long param_4, uint param_5, uint param_6, int param_7);

        private IntPtr activeBuffArray = IntPtr.Zero;

        private readonly IconReplacerAddressResolver Address;
        private readonly Hook<OnCheckIsIconReplaceableDelegate> checkerHook;
        private readonly ClientState clientState;

        private readonly IntPtr comboTimer;

        private readonly XIVComboConfiguration Configuration;

        private readonly HashSet<uint> customIds;
        private readonly HashSet<uint> vanillaIds;
        private HashSet<uint> noUpdateIcons;
        private HashSet<uint> seenNoUpdate;

        private readonly Hook<OnGetIconDelegate> iconHook;
        private readonly IntPtr lastComboMove;
        private readonly IntPtr playerLevel;
        private readonly IntPtr playerJob;
        private byte lastJob = 0;

        private readonly IntPtr BuffVTableAddr;
        private float ping;

        private unsafe delegate int* getArray(long* address);

        private bool shutdown;

        private Hook<OnRequestActionDetour> requestActionHook;
        private struct BuffInfo
        {
            public short buff;
            public short filler;
            public float duration;
            public int provider;
        }
        private int buffOffset = 0;
        private int BuffInfoSize = sizeof(short) + sizeof(short) + sizeof(float) + sizeof(int);

        private short lastDump = -1;

        public IconReplacer(SigScanner scanner, ClientState clientState, XIVComboConfiguration configuration)
        {
            ping = 0;
            shutdown = false;
            Configuration = configuration;
            this.clientState = clientState;

            Address = new IconReplacerAddressResolver();
            Address.Setup(scanner);

            comboTimer = scanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 80 7E 21 00", 0x178);
            lastComboMove = comboTimer + 0x4;

            playerLevel = scanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 88 45 EF", 0x4d) + 0x78;
            playerJob = playerLevel - 0xE;

            BuffVTableAddr = scanner.GetStaticAddressFromSig("48 89 05 ?? ?? ?? ?? 88 05 ?? ?? ?? ?? 88 05 ?? ?? ?? ??", 0);

            customIds = new HashSet<uint>();
            vanillaIds = new HashSet<uint>();
            noUpdateIcons = new HashSet<uint>();
            seenNoUpdate = new HashSet<uint>();

            PopulateDict();

            Log.Verbose("===== H O T B A R S =====");
            Log.Verbose("IsIconReplaceable address {IsIconReplaceable}", Address.IsIconReplaceable);
            Log.Verbose("GetIcon address {GetIcon}", Address.GetIcon);
            Log.Verbose("ComboTimer address {ComboTimer}", comboTimer);
            Log.Verbose("LastComboMove address {LastComboMove}", lastComboMove);
            Log.Verbose("PlayerLevel address {PlayerLevel}", playerLevel);

            iconHook = new Hook<OnGetIconDelegate>(Address.GetIcon, new OnGetIconDelegate(GetIconDetour), this);
            checkerHook = new Hook<OnCheckIsIconReplaceableDelegate>(Address.IsIconReplaceable,
                new OnCheckIsIconReplaceableDelegate(CheckIsIconReplaceableDetour), this);

            Task.Run(() =>
            {
                BuffTask();
            });
            requestActionHook = new Hook<OnRequestActionDetour>(Address.RequestAction, new OnRequestActionDetour(HandleRequestAction), this);
        }

        private unsafe ulong HandleRequestAction(long param_1, uint param_2, ulong param_3, long param_4, uint param_5, uint param_6, int param_7)
        {
            Log.Information($"Requested Action : {param_3} {param_4}");
            return this.requestActionHook.Original(param_1, param_2, param_3, param_4, param_5, param_6, param_7);
        }

        public void Enable()
        {
            iconHook.Enable();
            checkerHook.Enable();
            // requestActionHook.Enable();
        }

        public void Dispose()
        {
            shutdown = true;
            iconHook.Dispose();
            checkerHook.Dispose();
            // requestActionHook.Dispose();
        }

        public void AddNoUpdate(uint [] ids)
        {
            foreach (uint id in ids)
            {
                if (!noUpdateIcons.Contains(id))
                    noUpdateIcons.Add(id);
            }
        }

        public void RemoveNoUpdate(uint [] ids)
        {
            foreach (uint id in ids)
            {
                if (noUpdateIcons.Contains(id))
                    noUpdateIcons.Remove(id);
                if (seenNoUpdate.Contains(id))
                    seenNoUpdate.Remove(id);
            }
        }
        private async void BuffTask()
        {
            while (!shutdown)
            {
                UpdateBuffAddress();
                await Task.Delay(1000);
            }
        }

        // I hate this function. This is the dumbest function to exist in the game. Just return 1.
        // Determines which abilities are allowed to have their icons updated.
        private ulong CheckIsIconReplaceableDetour(uint actionID)
        {
            if (!noUpdateIcons.Contains(actionID))
            {
                return 1;
            }
            if (!seenNoUpdate.Contains(actionID)) { 
                return 1;
            }
            return 0;
        }

        private ulong MyCustomCombos(byte self, uint actionID)
        {
            var lastMove = Marshal.ReadInt32(lastComboMove);
            var comboTime = Marshal.ReadInt32(comboTimer);
            var level = Marshal.ReadByte(playerLevel);
            var job = clientState.LocalPlayer.ClassJob.Id;

            if (job == PLD.Job)
            {
                UpdateBuffAddress();
                // Holy Spirt => Confiteor (when Requiescat is down to 2s or less)
                if (level >= PLD.LevelConfiteor)
                {
                    if (actionID == PLD.HolySpirit)
                    {
                        if (SearchBuffArray(PLD.BuffRequiescat, 0, 2) || SearchBuffArray(PLD.BuffEnhancedRequiescat, 0, 2))
                            return PLD.Confiteor;
                    }

                    // Holy Circle => Confiteor (when Requiescat is down to 2s or less)
                    if (actionID == PLD.HolyCircle)
                    {
                        if (SearchBuffArray(PLD.BuffRequiescat, 0, 2) || SearchBuffArray(PLD.BuffEnhancedRequiescat, 0, 2))
                            return PLD.Confiteor;
                    }
                }
            }
            else if (job == MNK.Job)
            {
                UpdateBuffAddress();

                if (actionID == MNK.FistsOfFire)
                {
                    if (level >= MNK.LevelFistsOfWind && SearchBuffArray(MNK.BuffFistsOfFire))
                        return MNK.FistsOfWind;
                    return MNK.FistsOfFire;
                }

                // Rockbreaker => Arm of the Destoyer > Four-point Fury / Twin Snakes / True Strike > Rockbreaker / Snap Punch
                if (actionID == MNK.Rockbreaker)
                {
                    var gauge = clientState.JobGauges.Get<MNKGauge>();

                    if (level >= MNK.LevelRiddleOfWind && gauge.NumGLStacks >= 3 && !SearchBuffArray(MNK.BuffFistsOfWind))
                        return MNK.FistsOfWind;
                    if (level >= MNK.LevelFistsOfFire && gauge.NumGLStacks < 3 && !SearchBuffArray(MNK.BuffFistsOfFire))
                        return MNK.FistsOfFire;

                    if (SearchBuffArray(MNK.BuffPerfectBalance))
                        return MNK.Rockbreaker;

                    if (SearchBuffArray(MNK.BuffCoeurlForm))
                    {
                        if (level >= MNK.LevelRockbreaker)
                            return MNK.Rockbreaker;
                        return MNK.SnapPunch;
                    }

                    if (SearchBuffArray(MNK.BuffRaptorForm))
                    {
                        if (level >= MNK.LevelFourPointFury)
                            return MNK.FourPointFury;
                        if (level >= MNK.TwinSnakes && !SearchBuffArray(MNK.BuffTwinSnakes, 5))
                            return MNK.TwinSnakes;
                        return MNK.TrueStrike;
                    }

                    if (level >= MNK.LevelArmOfTheDestroyer)
                        return MNK.ArmOfTheDestroyer;

                    return MNK.Rockbreaker;
                }

                // Dragon Kick => Dragon Kick / Bootshine
                if (actionID == MNK.DragonKick)
                {
                    if (SearchBuffArray(MNK.BuffLeadenFist))
                        return MNK.Bootshine;
                    return level >= MNK.LevelDragonKick ? MNK.DragonKick : MNK.Bootshine;
                }

                // Snap Punch => Dragon Kick / Bootshine > Twin Snakes / True Strike > Snap Punch
                if (actionID == MNK.SnapPunch)
                {
                    var gauge = clientState.JobGauges.Get<MNKGauge>();

                    if (level >= MNK.LevelRiddleOfWind && gauge.NumGLStacks >= 3 && !SearchBuffArray(MNK.BuffFistsOfWind))
                        return MNK.FistsOfWind;
                    if (level >= MNK.LevelFistsOfFire && gauge.NumGLStacks < 3 && !SearchBuffArray(MNK.BuffFistsOfFire))
                        return MNK.FistsOfFire;

                    if (SearchBuffArray(MNK.BuffPerfectBalance) || SearchBuffArray(MNK.BuffCoeurlForm))
                        return MNK.SnapPunch;

                    if (SearchBuffArray(MNK.BuffRaptorForm))
                    {
                        if (SearchBuffArray(MNK.BuffTwinSnakes, 5))
                        {
                            return MNK.TrueStrike;
                        }
                        return level >= MNK.LevelTwinSnakes ? MNK.TwinSnakes : MNK.TrueStrike;
                    }

                    if (SearchBuffArray(MNK.BuffLeadenFist))
                        return MNK.Bootshine;
                    return level >= MNK.LevelDragonKick ? MNK.DragonKick : MNK.Bootshine;
                }
            }
            else if (job == RDM.Job)
            {
                UpdateBuffAddress();
                // Replace Verstone / Verfire with Scorch / Verholy / Verflare / Veraero / Verthunder / Jolt
                var gauge = clientState.JobGauges.Get<RDMGauge>();
                if (actionID == RDM.Verstone)
                {
                    if (comboTime > 0)
                    {
                        if (level >= RDM.LevelScorch && (lastMove == RDM.Verholy || lastMove == RDM.Verflare))
                            return RDM.Scorch;
                        if (level >= RDM.LevelVerholy && lastMove == RDM.ERedoublement)
                            return (gauge.WhiteGauge <= gauge.BlackGauge) ? RDM.Verholy : RDM.Verflare;
                    }
                    // If we have both Verfire Ready and Verstone Ready, evaluate based on gauges
                    if (level >= RDM.LevelVerfire && level >= RDM.LevelVerstone && SearchBuffArray(RDM.BuffVerfireReady) && SearchBuffArray(RDM.BuffVerstoneReady))
                        return (gauge.WhiteGauge <= gauge.BlackGauge) ? RDM.Verstone : RDM.Verfire;

                    if (level >= RDM.LevelVerstone && SearchBuffArray(RDM.BuffVerstoneReady))
                        return RDM.Verstone;

                    // If we have Dualcast or Swiftcast up, evaluate based on gauges
                    if (level >= RDM.LevelVeraero && level >= RDM.LevelVerthunder && (SearchBuffArray(RDM.BuffDualCast) || SearchBuffArray(RDM.BuffSwiftCast)))
                        return (gauge.WhiteGauge <= gauge.BlackGauge) ? RDM.Veraero : RDM.Verthunder;

                    if (level >= RDM.LevelVeraero && (SearchBuffArray(RDM.BuffDualCast) || SearchBuffArray(RDM.BuffSwiftCast)))
                        return RDM.Veraero;

                    return (level >= RDM.LevelJolt2) ? RDM.Jolt2 : RDM.Jolt;
                }
                if (actionID == RDM.Verfire)
                {
                    if (comboTime > 0)
                    {
                        if (level >= RDM.LevelScorch && (lastMove == RDM.Verholy || lastMove == RDM.Verflare))
                            return RDM.Scorch;
                        if (level >= RDM.LevelVerflare && lastMove == RDM.ERedoublement)
                            return (gauge.BlackGauge > gauge.WhiteGauge && level >= RDM.LevelVerholy) ? RDM.Verholy : RDM.Verflare;
                    }
                    // If we have both Verfire Ready and Verstone Ready, evaluate based on gauges
                    if (level >= RDM.LevelVerfire && level >= RDM.LevelVerstone && SearchBuffArray(RDM.BuffVerfireReady) && SearchBuffArray(RDM.BuffVerstoneReady))
                        return (gauge.BlackGauge <= gauge.WhiteGauge) ? RDM.Verfire : RDM.Verstone;

                    if (level >= RDM.LevelVerfire && SearchBuffArray(RDM.BuffVerfireReady))
                        return RDM.Verfire;

                    // If we have Dualcast or Swiftcast up, evaluate based on gauges
                    if (level >= RDM.LevelVeraero && level >= RDM.LevelVerthunder && (SearchBuffArray(RDM.BuffDualCast) || SearchBuffArray(RDM.BuffSwiftCast)))
                        return (gauge.BlackGauge <= gauge.WhiteGauge) ? RDM.Verthunder : RDM.Veraero;

                    if (level >= RDM.LevelVerthunder && (SearchBuffArray(RDM.BuffDualCast) || SearchBuffArray(RDM.BuffSwiftCast)))
                        return RDM.Verthunder;
                    return (level >= RDM.LevelJolt2) ? RDM.Jolt2 : RDM.Jolt;
                }
            } else if (job == GNB.Job) {
                var gauge = clientState.JobGauges.Get<GNBGauge>();

                if (actionID == GNB.Continuation)
                {
                    if (level >= GNB.LevelContinuation)
                    {
                        UpdateBuffAddress();
                        if (SearchBuffArray(GNB.BuffReadyToRip))
                            return GNB.JugularRip;
                        if (SearchBuffArray(GNB.BuffReadyToTear))
                            return GNB.AbdomenTear;
                        if (SearchBuffArray(GNB.BuffReadyToGouge))
                            return GNB.EyeGouge;
                    }
                    switch (gauge.AmmoComboStepNumber)
                    {
                        case 1:
                            return GNB.SavageClaw;
                        case 2:
                            return GNB.WickedTalon;
                        default:
                            return GNB.GnashingFang;
                    }
                }
            } else if (job == DNC.Job) {
                UpdateBuffAddress();
                var gauge = clientState.JobGauges.Get<DNCGauge>();

                if (actionID == DNC.TechnicalStep)
                {
                    if (gauge.IsDancing() && SearchBuffArray(DNC.BuffTechnicalStep))
                    {
                        if (gauge.NumCompleteSteps >= 4)
                            return DNC.TechnicalFinish4;
                        return gauge.NextStep();
                    }
                    return DNC.TechnicalStep;
                }

                if (actionID == DNC.StandardStep)
                {
                    if (gauge.IsDancing() && SearchBuffArray(DNC.BuffStandardStep))
                    {
                        if (gauge.NumCompleteSteps >= 2)
                            return DNC.StandardFinish2;
                        return gauge.NextStep();
                    }
                    return DNC.StandardStep;
                }

                // Single Target - focus on building procs
                if (actionID == DNC.ReverseCascade)
                {
                    if (level >= DNC.LevelFountainfall && SearchBuffArray(DNC.BuffFlourishingFountain, 0, 3))
                        return DNC.Fountainfall;

                    if (level >= DNC.LevelReverseCascade && SearchBuffArray(DNC.BuffFlourishingCascade, 0, 3))
                        return DNC.ReverseCascade;

                    if (level >= DNC.LevelFountainfall && SearchBuffArray(DNC.BuffFlourishingFountain))
                        return DNC.Fountainfall;

                    if (level >= DNC.LevelFountain && comboTime > 0 && comboTime <= 3 && lastMove == DNC.Cascade)
                        return DNC.Fountain;

                    if (level >= DNC.LevelReverseCascade && SearchBuffArray(DNC.BuffFlourishingCascade))
                        return DNC.ReverseCascade;

                    if (level >= DNC.LevelFountain && comboTime > 0 && lastMove == DNC.Cascade)
                        return DNC.Fountain;

                    return DNC.Cascade;
                }

                // Single Target - focus on burning procs
                if (actionID == DNC.Fountainfall)
                {

                    if (level >= DNC.LevelFountainfall && SearchBuffArray(DNC.BuffFlourishingFountain, 0,3))
                        return DNC.Fountainfall;

                    if (level >= DNC.LevelBloodshower && SearchBuffArray(DNC.BuffFlourishingShower, 0, 3))
                        return DNC.Bloodshower;

                    if (level >= DNC.LevelRisingWindmill && SearchBuffArray(DNC.BuffFlourishingWindmill, 0, 3))
                        return DNC.RisingWindmill;

                    if (level >= DNC.LevelReverseCascade && SearchBuffArray(DNC.BuffFlourishingCascade, 0, 3))
                        return DNC.ReverseCascade;

                    if (level >= DNC.LevelSaberDance && gauge.Esprit > 80)
                        return DNC.SaberDance;

                    if (level >= DNC.LevelSaberDance && gauge.Esprit > 50 && SearchBuffArray(DNC.BuffTechnicalFinish))
                        return DNC.SaberDance;

                    if (level >= DNC.LevelFountainfall && SearchBuffArray(DNC.BuffFlourishingFountain))
                        return DNC.Fountainfall;

                    if (level >= DNC.LevelFountain && comboTime > 0 && comboTime <= 3 && lastMove == DNC.Cascade)
                        return DNC.Fountain;

                    if (level >= DNC.LevelBloodshower && SearchBuffArray(DNC.BuffFlourishingShower))
                        return DNC.Bloodshower;

                    if (level >= DNC.LevelRisingWindmill && SearchBuffArray(DNC.BuffFlourishingWindmill))
                        return DNC.RisingWindmill;

                    if (level >= DNC.LevelReverseCascade && SearchBuffArray(DNC.BuffFlourishingCascade))
                        return DNC.ReverseCascade;

                    if (level >= DNC.LevelFountain && comboTime > 0 && lastMove == DNC.Cascade)
                        return DNC.Fountain;

                    return DNC.Cascade;
                }

                // AE - with ST spenders
                if (actionID == DNC.RisingWindmill)
                {
                    if (level >= DNC.LevelFountainfall && SearchBuffArray(DNC.BuffFlourishingFountain, 0, 3))
                        return DNC.Fountainfall;

                    if (level >= DNC.LevelBloodshower && SearchBuffArray(DNC.BuffFlourishingShower, 0, 3))
                        return DNC.Bloodshower;

                    if (level >= DNC.LevelRisingWindmill && SearchBuffArray(DNC.BuffFlourishingWindmill, 0, 3))
                        return DNC.RisingWindmill;

                    if (level >= DNC.LevelReverseCascade && SearchBuffArray(DNC.BuffFlourishingCascade, 0, 3))
                        return DNC.ReverseCascade;

                    if (level >= DNC.LevelFountainfall && SearchBuffArray(DNC.BuffFlourishingFountain))
                        return DNC.Fountainfall;

                    if (level >= DNC.LevelBloodshower && SearchBuffArray(DNC.BuffFlourishingShower))
                        return DNC.Bloodshower;

                    if (level >= DNC.LevelBladeshower && comboTime > 0 && comboTime <= 3 && lastMove == DNC.Windmill)
                        return DNC.Bladeshower;

                    if (level >= DNC.LevelRisingWindmill && SearchBuffArray(DNC.BuffFlourishingWindmill))
                        return DNC.RisingWindmill;

                    if (level >= DNC.LevelReverseCascade && SearchBuffArray(DNC.BuffFlourishingCascade))
                        return DNC.ReverseCascade;

                    if (level >= DNC.LevelBladeshower && comboTime > 0 && lastMove == DNC.Windmill)
                        return DNC.Bladeshower;

                    return DNC.Windmill;
                }

                // AE - without ST spenders
                if (actionID == DNC.Bloodshower)
                {

                    if (level >= DNC.LevelBloodshower && SearchBuffArray(DNC.BuffFlourishingShower, 0, 3))
                        return DNC.Bloodshower;

                    if (level >= DNC.LevelRisingWindmill && SearchBuffArray(DNC.BuffFlourishingWindmill, 0, 3))
                        return DNC.RisingWindmill;

                    if (level >= DNC.LevelSaberDance && gauge.Esprit > 80)
                        return DNC.SaberDance;

                    if (level >= DNC.LevelSaberDance && gauge.Esprit > 50 && SearchBuffArray(DNC.BuffTechnicalFinish))
                        return DNC.SaberDance;

                    if (level >= DNC.LevelBloodshower && SearchBuffArray(DNC.BuffFlourishingShower))
                        return DNC.Bloodshower;

                    if (level >= DNC.LevelBladeshower && comboTime > 0 && comboTime <= 3 && lastMove == DNC.Windmill)
                        return DNC.Bladeshower;

                    if (level >= DNC.LevelRisingWindmill && SearchBuffArray(DNC.BuffFlourishingWindmill))
                        return DNC.RisingWindmill;

                    if (level >= DNC.LevelBladeshower && comboTime > 0 && lastMove == DNC.Windmill)
                        return DNC.Bladeshower;

                    return DNC.Windmill;
                }

            }

            throw new Exception("Not my custom combo");
        }

        /// <summary>
        ///     Replace an ability with another ability
        ///     actionID is the original ability to be "used"
        ///     Return either actionID (itself) or a new Action table ID as the
        ///     ability to take its place.
        ///     I tend to make the "combo chain" button be the last move in the combo
        ///     For example, Souleater combo on DRK happens by dragging Souleater
        ///     onto your bar and mashing it.
        /// </summary>
        private ulong GetIconDetour(byte self, uint actionID)
        {
            if (lastJob != Marshal.ReadByte(playerJob))
            {
                lastJob = Marshal.ReadByte(playerJob);
                seenNoUpdate.Clear();
            }
            // TODO: More jobs, level checking for everything.
            if (noUpdateIcons.Contains(actionID) && !seenNoUpdate.Contains(actionID))
            {
                seenNoUpdate.Add(actionID);
                return actionID;
            }
            if (vanillaIds.Contains(actionID)) return iconHook.Original(self, actionID);
            if (!customIds.Contains(actionID)) return actionID;
            if (activeBuffArray == IntPtr.Zero) return iconHook.Original(self, actionID);

            try
            {
                return MyCustomCombos(self, actionID);
            }
            catch (Exception)
            {

            }

            // Don't clutter the spaghetti any worse than it already is.
            var lastMove = Marshal.ReadInt32(lastComboMove);
            var comboTime = Marshal.PtrToStructure<float>(comboTimer);
            var level = Marshal.ReadByte(playerLevel);

            // DRAGOON

            // Change Jump/High Jump into Mirage Dive when Dive Ready
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DragoonJumpFeature))
                if (actionID == DRG.Jump)
                {
                    if (SearchBuffArray(1243))
                        return DRG.MirageDive;
                    if (level >= 74)
                        return DRG.HighJump;
                    return DRG.Jump;
                }

            // Change Blood of the Dragon into Stardiver when in Life of the Dragon
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DragoonBOTDFeature))
                if (actionID == DRG.BOTD)
                {
                    if (level >= 80)
                        if (clientState.JobGauges.Get<DRGGauge>().BOTDState == BOTDState.LOTD)
                            return DRG.Stardiver;
                    return DRG.BOTD;
                    
                }

            // Replace Coerthan Torment with Coerthan Torment combo chain
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DragoonCoerthanTormentCombo))
                if (actionID == DRG.CTorment)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == DRG.DoomSpike && level >= 62)
                            return DRG.SonicThrust;
                        if (lastMove == DRG.SonicThrust && level >= 72)
                            return DRG.CTorment;
                    }

                    return DRG.DoomSpike;
                }


            // Replace Chaos Thrust with the Chaos Thrust combo chain
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DragoonChaosThrustCombo))
                if (actionID == DRG.ChaosThrust)
                {
                    if (comboTime > 0)
                    {
                        if ((lastMove == DRG.TrueThrust || lastMove == DRG.RaidenThrust)
                            && level >= 18) 
                                return DRG.Disembowel;
                        if (lastMove == DRG.Disembowel && level >= 50) 
                            return DRG.ChaosThrust;
                    }
                    if (SearchBuffArray(802) && level >= 56)
                        return DRG.FangAndClaw;
                    if (SearchBuffArray(803) && level >= 58)
                        return DRG.WheelingThrust;
                    if (SearchBuffArray(1863) && level >= 76)
                        return DRG.RaidenThrust;

                    return DRG.TrueThrust;
                }


            // Replace Full Thrust with the Full Thrust combo chain
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DragoonFullThrustCombo))
                if (actionID == 84)
                {
                    if (comboTime > 0)
                    {
                        if ((lastMove == DRG.TrueThrust || lastMove == DRG.RaidenThrust)
                            && level >= 4)
                            return DRG.VorpalThrust;
                        if (lastMove == DRG.VorpalThrust && level >= 26)
                            return DRG.FullThrust;
                    }
                    if (SearchBuffArray(802) && level >= 56)
                        return DRG.FangAndClaw;
                    if (SearchBuffArray(803) && level >= 58)
                        return DRG.WheelingThrust;
                    if (SearchBuffArray(1863) && level >= 76)
                        return DRG.RaidenThrust;

                    return DRG.TrueThrust;
                }

            // DARK KNIGHT

            // Replace Souleater with Souleater combo chain
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DarkSouleaterCombo))
                if (actionID == DRK.Souleater)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == DRK.HardSlash && level >= 2)
                            return DRK.SyphonStrike;
                        if (lastMove == DRK.SyphonStrike && level >= 26)
                            return DRK.Souleater;
                    }

                    return DRK.HardSlash;
                }

            // Replace Stalwart Soul with Stalwart Soul combo chain
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DarkStalwartSoulCombo))
                if (actionID == DRK.StalwartSoul)
                {
                    if (comboTime > 0)
                        if (lastMove == DRK.Unleash && level >= 72)
                            return DRK.StalwartSoul;

                    return DRK.Unleash;
                }

            // PALADIN

            // Replace Goring Blade with Goring Blade combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.PaladinGoringBladeCombo))
                if (actionID == PLD.GoringBlade)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == PLD.FastBlade && level >= 4)
                            return PLD.RiotBlade;
                        if (lastMove == PLD.RiotBlade && level >= 54)
                            return PLD.GoringBlade;
                    }

                    return PLD.FastBlade;
                }

            // Replace Royal Authority with Royal Authority combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.PaladinRoyalAuthorityCombo))
                if (actionID == PLD.RoyalAuthority || actionID == PLD.RageOfHalone)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == PLD.FastBlade && level >= 4)
                            return PLD.RiotBlade;
                        if (lastMove == PLD.RiotBlade)
                        {
                            if (level >= 60)
                                return PLD.RoyalAuthority;
                            if (level >= 26)
                                return PLD.RageOfHalone;
                        }
                    }

                    return PLD.FastBlade;
                }

            // Replace Prominence with Prominence combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.PaladinProminenceCombo))
                if (actionID == PLD.Prominence)
                {
                    if (comboTime > 0)
                        if (lastMove == PLD.TotalEclipse && level >= 40)
                            return PLD.Prominence;

                    return PLD.TotalEclipse;
                }
            
            // Replace Requiescat with Confiteor when under the effect of Requiescat
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.PaladinRequiescatCombo))
                if (actionID == PLD.Requiescat)
                {
                    if (SearchBuffArray(1368) && level >= 80)
                        return PLD.Confiteor;
                    return PLD.Requiescat;
                }

            // WARRIOR

            // Replace Storm's Path with Storm's Path combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.WarriorStormsPathCombo))
                if (actionID == WAR.StormsPath)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == WAR.HeavySwing && level >= 4)
                            return WAR.Maim;
                        if (lastMove == WAR.Maim && level >= 26)
                            return WAR.StormsPath;
                    }

                    return 31;
                }

            // Replace Storm's Eye with Storm's Eye combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.WarriorStormsEyeCombo))
                if (actionID == WAR.StormsEye)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == WAR.HeavySwing && level >= 4)
                            return WAR.Maim;
                        if (lastMove == WAR.Maim && level >= 50)
                            return WAR.StormsEye;
                    }

                    return WAR.HeavySwing;
                }

            // Replace Mythril Tempest with Mythril Tempest combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.WarriorMythrilTempestCombo))
                if (actionID == WAR.MythrilTempest)
                {
                    if (comboTime > 0)
                        if (lastMove == WAR.Overpower && level >= 40)
                            return WAR.MythrilTempest;
                    return WAR.Overpower;
                }

            // SAMURAI

            // Replace Yukikaze with Yukikaze combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SamuraiYukikazeCombo))
                if (actionID == SAM.Yukikaze)
                {
                    if (SearchBuffArray(SAM.BuffMeikyoShisui))
                        return SAM.Yukikaze;
                    if (comboTime > 0)
                        if (lastMove == SAM.Hakaze && level >= SAM.LevelYukikaze)
                            return SAM.Yukikaze;
                    return SAM.Hakaze;
                }

            // Replace Gekko with Gekko combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SamuraiGekkoCombo))
                if (actionID == SAM.Gekko)
                {
                    if (SearchBuffArray(SAM.BuffMeikyoShisui))
                        return SAM.Gekko;
                    if (comboTime > 0)
                    {
                        if (lastMove == SAM.Hakaze && level >= SAM.LevelJinpu)
                            return SAM.Jinpu;
                        if (lastMove == SAM.Jinpu && level >= SAM.LevelGekko)
                            return SAM.Gekko;
                    }

                    return SAM.Hakaze;
                }

            // Replace Kasha with Kasha combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SamuraiKashaCombo))
                if (actionID == SAM.Kasha)
                {
                    if (SearchBuffArray(SAM.BuffMeikyoShisui))
                        return SAM.Kasha;
                    if (comboTime > 0)
                    {
                        if (lastMove == SAM.Hakaze && level >= SAM.LevelShifu)
                            return SAM.Shifu;
                        if (lastMove == SAM.Shifu && level >= SAM.LevelKasha)
                            return SAM.Kasha;
                    }

                    return SAM.Hakaze;
                }

            // Replace Mangetsu with Mangetsu combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SamuraiMangetsuCombo))
                if (actionID == SAM.Mangetsu)
                {
                    if (SearchBuffArray(SAM.BuffMeikyoShisui))
                        return SAM.Mangetsu;
                    if (comboTime > 0)
                        if (lastMove == SAM.Fuga && level >= SAM.LevelMangetsu)
                            return SAM.Mangetsu;
                    return SAM.Fuga;
                }

            // Replace Oka with Oka combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SamuraiOkaCombo))
                if (actionID == SAM.Oka)
                {
                    if (SearchBuffArray(SAM.BuffMeikyoShisui))
                        return SAM.Oka;
                    if (comboTime > 0)
                        if (lastMove == SAM.Fuga && level >= SAM.LevelOka)
                            return SAM.Oka;
                    return SAM.Fuga;
                }

            // Turn Seigan into Third Eye when not procced
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SamuraiThirdEyeFeature))
                if (actionID == SAM.Seigan) {
                    if (SearchBuffArray(SAM.BuffEyesOpen)) return SAM.Seigan;
                    return SAM.ThirdEye;
                }

            // NINJA

            // Replace Armor Crush with Armor Crush combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.NinjaArmorCrushCombo))
                if (actionID == NIN.ArmorCrush)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == NIN.SpinningEdge && level >= 4)
                            return NIN.GustSlash;
                        if (lastMove == NIN.GustSlash && level >= 54)
                            return NIN.ArmorCrush;
                    }

                    return NIN.SpinningEdge;
                }

            // Replace Aeolian Edge with Aeolian Edge combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.NinjaAeolianEdgeCombo))
                if (actionID == NIN.AeolianEdge)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == NIN.SpinningEdge && level >= 4)
                            return NIN.GustSlash;
                        if (lastMove == NIN.GustSlash && level >= 26)
                            return NIN.AeolianEdge;
                    }

                    return NIN.SpinningEdge;
                }

            // Replace Hakke Mujinsatsu with Hakke Mujinsatsu combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.NinjaHakkeMujinsatsuCombo))
                if (actionID == NIN.HakkeM)
                {
                    if (comboTime > 0)
                        if (lastMove == NIN.DeathBlossom && level >= 52)
                            return NIN.HakkeM;
                    return NIN.DeathBlossom;
                }

            //Replace Dream Within a Dream with Assassinate when Assassinate Ready
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.NinjaAssassinateFeature))
                if (actionID == NIN.DWAD)
                {
                    if (SearchBuffArray(1955)) return NIN.Assassinate;
                    return NIN.DWAD;
                }

            // GUNBREAKER

            // Replace Solid Barrel with Solid Barrel combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.GunbreakerSolidBarrelCombo) && actionID == GNB.SolidBarrel)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == GNB.KeenEdge && level >= GNB.LevelBrutalShell)
                            return GNB.BrutalShell;
                        if (lastMove == GNB.BrutalShell && level >= GNB.LevelSolidBarrel)
                            return GNB.SolidBarrel;
                    }

                    return GNB.KeenEdge;
                }

            // Replace Wicked Talon with Gnashing Fang combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.GunbreakerGnashingFangCombo) && actionID == GNB.WickedTalon)
            {
                var ammoComboState = clientState.JobGauges.Get<GNBGauge>().AmmoComboStepNumber;
                switch(ammoComboState)
                {
                    case 1:
                        return GNB.SavageClaw;
                    case 2:
                        return GNB.WickedTalon;
                    default:
                        return GNB.GnashingFang;
                }
            }

            // Replace Continuation with Gnashing Fang combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.GunbreakerGnashingFangCont) && actionID == GNB.Continuation)
            {
                if (level >= GNB.LevelContinuation)
                {
                    UpdateBuffAddress();
                    if (SearchBuffArray(GNB.BuffReadyToRip))
                        return GNB.JugularRip;
                    if (SearchBuffArray(GNB.BuffReadyToTear))
                        return GNB.AbdomenTear;
                    if (SearchBuffArray(GNB.BuffReadyToGouge))
                        return GNB.EyeGouge;
                }
                var ammoComboState = clientState.JobGauges.Get<GNBGauge>().AmmoComboStepNumber;
                switch (ammoComboState)
                {
                    case 1:
                        return GNB.SavageClaw;
                    case 2:
                        return GNB.WickedTalon;
                    default:
                        return GNB.GnashingFang;
                }
            }


            // Replace Demon Slaughter with Demon Slaughter combo
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.GunbreakerDemonSlaughterCombo) && actionID == GNB.DemonSlaughter)
            {
                if (comboTime > 0)
                    if (lastMove == GNB.DemonSlice && level >= 40)
                        return GNB.DemonSlaughter;
                return GNB.DemonSlice;
            }

            // MACHINIST

            // Replace Clean Shot with Heated Clean Shot combo
            // Or with Heat Blast when overheated.
            // For some reason the shots use their unheated IDs as combo moves
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.MachinistMainCombo))
                if (actionID == MCH.CleanShot || actionID == MCH.HeatedCleanShot)
                {
                    if (comboTime > 0)
                    {
                        if (lastMove == MCH.SplitShot)
                        {
                            if (level >= 60)
                                return MCH.HeatedSlugshot;
                            if (level >= 2)
                                return MCH.SlugShot;
                        }

                        if (lastMove == MCH.SlugShot)
                        {
                            if (level >= 64)
                                return MCH.HeatedCleanShot;
                            if (level >= 26)
                                return MCH.CleanShot;
                        }
                    }

                    if (level >= 54)
                        return MCH.HeatedSplitShot;
                    return MCH.SplitShot;
                }

                        
            // Replace Hypercharge with Heat Blast when overheated
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.MachinistOverheatFeature))
                if (actionID == MCH.Hypercharge) {
                    var gauge = clientState.JobGauges.Get<MCHGauge>();
                    if (gauge.IsOverheated() && level >= 35)
                        return MCH.HeatBlast;
                    return MCH.Hypercharge;
                }
                
            // Replace Spread Shot with Auto Crossbow when overheated.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.MachinistSpreadShotFeature))
                if (actionID == MCH.SpreadShot)
                {
                    if (clientState.JobGauges.Get<MCHGauge>().IsOverheated() && level >= 52)
                        return MCH.AutoCrossbow;
                    return MCH.SpreadShot;
                }

            // BLACK MAGE

            // Enochian changes to B4 or F4 depending on stance.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.BlackEnochianFeature))
                if (actionID == BLM.Enochian)
                {
                    var gauge = clientState.JobGauges.Get<BLMGauge>();
                    if (gauge.IsEnoActive())
                    {
                        if (gauge.InUmbralIce() && level >= 58)
                            return BLM.Blizzard4;
                        if (level >= 60)
                            return BLM.Fire4;
                    }

                    return BLM.Enochian;
                }

            // Umbral Soul and Transpose
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.BlackManaFeature))
                if (actionID == BLM.Transpose)
                {
                    var gauge = clientState.JobGauges.Get<BLMGauge>();
                    if (gauge.InUmbralIce() && gauge.IsEnoActive() && level >= 76)
                        return BLM.UmbralSoul;
                    return BLM.Transpose;
                }

            // Ley Lines and BTL
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.BlackLeyLines))
                if (actionID == BLM.LeyLines)
                {
                    if (SearchBuffArray(737) && level >= 62)
                        return BLM.BTL;
                    return BLM.LeyLines;
                }

            // ASTROLOGIAN

            // Make cards on the same button as play
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.AstrologianCardsOnDrawFeature))
                if (actionID == AST.Play)
                {
                    var gauge = clientState.JobGauges.Get<ASTGauge>();
                    switch (gauge.DrawnCard())
                    {
                        case CardType.BALANCE:
                            return AST.Balance;
                        case CardType.BOLE:
                            return AST.Bole;
                        case CardType.ARROW:
                            return AST.Arrow;
                        case CardType.SPEAR:
                            return AST.Spear;
                        case CardType.EWER:
                            return AST.Ewer;
                        case CardType.SPIRE:
                            return AST.Spire;
                        /*
                        case CardType.LORD:
                            return 7444;
                        case CardType.LADY:
                            return 7445;
                        */
                        default:
                            return AST.Draw;
                    }
                }

            // SUMMONER

            // DWT changes. 
            // Now contains DWT, Deathflare, Summon Bahamut, Enkindle Bahamut, FBT, and Enkindle Phoenix.
            // What a monster of a button.
            /*
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SummonerDwtCombo))
                if (actionID == 3581)
                {
                    var gauge = clientState.JobGauges.Get<SMNGauge>();
                    if (gauge.TimerRemaining > 0)
                    {
                        if (gauge.ReturnSummon > 0)
                        {
                            if (gauge.IsPhoenixReady()) return 16516;
                            return 7429;
                        }

                        if (level >= 60) return 3582;
                    }
                    else
                    {
                        if (gauge.IsBahamutReady()) return 7427;
                        if (gauge.IsPhoenixReady())
                        {
                            if (level == 80) return 16549;
                            return 16513;
                        }

                        return 3581;
                    }
                }
                */
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SummonerDemiCombo))
            {

                // Replace Deathflare with demi enkindles
                if (actionID == SMN.Deathflare)
                {
                    var gauge = clientState.JobGauges.Get<SMNGauge>();
                    if (gauge.IsPhoenixReady())
                        return SMN.EnkindlePhoenix;
                    if (gauge.TimerRemaining > 0 && gauge.ReturnSummon != SummonPet.NONE)
                        return SMN.EnkindleBahamut;
                    return SMN.Deathflare;
                }

                //Replace DWT with demi summons
                if (actionID == SMN.DWT)
                {
                    var gauge = clientState.JobGauges.Get<SMNGauge>();
                    if (gauge.IsBahamutReady())
                        return SMN.SummonBahamut;
                    if (gauge.IsPhoenixReady() ||
                        gauge.TimerRemaining > 0 && gauge.ReturnSummon != SummonPet.NONE)
                    {
                        if (level >= 80)
                            return SMN.FBTHigh;
                        return SMN.FBTLow;
                    }
                    return SMN.DWT;
                }
            }

            // Ruin 1 now upgrades to Brand of Purgatory in addition to Ruin 3 and Fountain of Fire
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SummonerBoPCombo))
                if (actionID == SMN.Ruin1 || actionID == SMN.Ruin3)
                {
                    var gauge = clientState.JobGauges.Get<SMNGauge>();
                    if (gauge.TimerRemaining > 0)
                        if (gauge.IsPhoenixReady())
                        {
                            if (SearchBuffArray(1867))
                                return SMN.BrandOfPurgatory;
                            return SMN.FountainOfFire;
                        }

                    if (level >= 54)
                        return SMN.Ruin3;
                    return SMN.Ruin1;
                }

            // Change Fester into Energy Drain
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SummonerEDFesterCombo))
                if (actionID == SMN.Fester)
                {
                    if (!clientState.JobGauges.Get<SMNGauge>().HasAetherflowStacks())
                        return SMN.EnergyDrain;
                    return SMN.Fester;
                }

            //Change Painflare into Energy Syphon
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.SummonerESPainflareCombo))
                if (actionID == SMN.Painflare)
                {
                    if (!clientState.JobGauges.Get<SMNGauge>().HasAetherflowStacks())
                        return SMN.EnergySyphon;
                    if (level >= 52)
                        return SMN.Painflare;
                    return SMN.EnergySyphon;
                }

            // SCHOLAR

            // Change Fey Blessing into Consolation when Seraph is out.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.ScholarSeraphConsolationFeature))
                if (actionID == SCH.FeyBless)
                {
                    if (clientState.JobGauges.Get<SCHGauge>().SeraphTimer > 0) return SCH.Consolation;
                    return SCH.FeyBless;
                }

            // Change Energy Drain into Aetherflow when you have no more Aetherflow stacks.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.ScholarEnergyDrainFeature))
                if (actionID == SCH.EnergyDrain)
                {
                    if (clientState.JobGauges.Get<SCHGauge>().NumAetherflowStacks == 0) return SCH.Aetherflow;
                    return SCH.EnergyDrain;
                }

            // DANCER

            // AoE GCDs are split into two buttons, because priority matters
            // differently in different single-target moments. Thanks yoship.
            // Replaces each GCD with its procced version.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DancerAoeGcdFeature))
            {
                if (actionID == DNC.Bloodshower)
                {
                    if (SearchBuffArray(DNC.BuffFlourishingShower))
                        return DNC.Bloodshower;
                    return DNC.Bladeshower;
                }

                if (actionID == DNC.RisingWindmill)
                {
                    if (SearchBuffArray(DNC.BuffFlourishingWindmill))
                        return DNC.RisingWindmill;
                    return DNC.Windmill;
                }
            }

            // Fan Dance changes into Fan Dance 3 while flourishing.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.DancerFanDanceCombo))
            {
                if (actionID == DNC.FanDance1)
                {
                    if (SearchBuffArray(1820))
                        return DNC.FanDance3;
                    return DNC.FanDance1;
                }

                // Fan Dance 2 changes into Fan Dance 3 while flourishing.
                if (actionID == DNC.FanDance2)
                {
                    if (SearchBuffArray(1820))
                        return DNC.FanDance3;
                    return DNC.FanDance2;
                }
            }

            // WHM

            // Replace Solace with Misery when full blood lily
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.WhiteMageSolaceMiseryFeature))
                if (actionID == WHM.Solace)
                {
                    if (clientState.JobGauges.Get<WHMGauge>().NumBloodLily == 3)
                        return WHM.Misery;
                    return WHM.Solace;
                }

            // Replace Solace with Misery when full blood lily
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.WhiteMageRaptureMiseryFeature))
                if (actionID == WHM.Rapture)
                {
                    if (clientState.JobGauges.Get<WHMGauge>().NumBloodLily == 3)
                        return WHM.Misery;
                    return WHM.Rapture;
                }

            // BARD

            // Replace Wanderer's Minuet with PP when in WM.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.BardWandererPPFeature))
                if (actionID == BRD.WanderersMinuet)
                {
                    if (clientState.JobGauges.Get<BRDGauge>().ActiveSong == CurrentSong.WANDERER)
                        return BRD.PitchPerfect;
                    return BRD.WanderersMinuet;
                }

            // Replace HS/BS with SS/RA when procced.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.BardStraightShotUpgradeFeature))
                if (actionID == BRD.HeavyShot || actionID == BRD.BurstShot)
                {
                    if (SearchBuffArray(122))
                    {
                        if (level >= 70) return BRD.RefulgentArrow;
                        return BRD.StraightShot;
                    }

                    if (level >= 76) return BRD.BurstShot;
                    return BRD.HeavyShot;
                }

            // MONK
            
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.MnkAoECombo))
                if (actionID == MNK.Rockbreaker)
                {
                    UpdateBuffAddress();
                    if (SearchBuffArray(110)) return MNK.Rockbreaker;
                    if (SearchBuffArray(107)) return MNK.AOTD;
                    if (SearchBuffArray(108)) return MNK.FourPointFury;
                    if (SearchBuffArray(109)) return MNK.Rockbreaker;
                    return MNK.AOTD;
                }

            // RED MAGE

            // Replace Veraero/thunder 2 with Impact when Dualcast is active
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.RedMageAoECombo))
            {
                var gauge = clientState.JobGauges.Get<RDMGauge>();
                if (actionID == RDM.Veraero2)
                {
                    if (SearchBuffArray(RDM.BuffSwiftCast) || SearchBuffArray(RDM.BuffDualCast))
                        return (level >= RDM.LevelImpact) ? RDM.Impact : RDM.Scatter;

                    return gauge.BlackGauge >= gauge.WhiteGauge ? RDM.Veraero2 : RDM.Verthunder2;
                }

                if (actionID == RDM.Verthunder2)
                {
                    if (SearchBuffArray(RDM.BuffSwiftCast) || SearchBuffArray(RDM.BuffDualCast))
                        return (level >= RDM.LevelImpact) ? RDM.Impact : RDM.Scatter;

                    return gauge.BlackGauge > gauge.WhiteGauge ? RDM.Veraero2 : RDM.Verthunder2;
                }
            }

            // Replace Redoublement with Redoublement combo, Enchanted if possible.
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.RedMageMeleeCombo))
                if (actionID == RDM.Redoublement)
                {
                    var gauge = clientState.JobGauges.Get<RDMGauge>();
                    if ((lastMove == RDM.Riposte || lastMove == RDM.ERiposte) && level >= RDM.LevelZwerchhau)
                    {
                        return (gauge.BlackGauge >= 25 && gauge.WhiteGauge >= 25) ? RDM.EZwerchhau : RDM.Zwerchhau;
                    }

                    if (lastMove == RDM.Zwerchhau && level >= RDM.LevelRedoublement)
                    {
                        return (gauge.BlackGauge >= 25 && gauge.WhiteGauge >= 25) ? RDM.ERedoublement : RDM.Redoublement;
                    }

                    return (gauge.BlackGauge >= 30 && gauge.WhiteGauge >= 30) ? RDM.ERiposte : RDM.Riposte;
                }
            if (Configuration.ComboPresets.HasFlag(CustomComboPreset.RedMageVerprocCombo))
            {
                if (actionID == RDM.Verstone)
                {
                    if (level >= 80 && (lastMove == RDM.Verflare || lastMove == RDM.Verholy)) return RDM.Scorch;
                    UpdateBuffAddress();
                    if (SearchBuffArray(1235)) return RDM.Verstone;
                    if (level < 62) return RDM.Jolt;
                    return RDM.Jolt2;
                }
                if (actionID == RDM.Verfire)
                {
                    if (level >= 80 && (lastMove == RDM.Verflare || lastMove == RDM.Verholy)) return RDM.Scorch;
                    UpdateBuffAddress();
                    if (SearchBuffArray(1234)) return RDM.Verfire;
                    if (level < 62) return RDM.Jolt;
                    return RDM.Jolt2;
                }
            }

            return iconHook.Original(self, actionID);
        }

        private void DumpBuffArray(short foundBuff)
        {
            if (lastDump != foundBuff)
            {
                Log.Information($"Dumping >> {foundBuff}");
                var buffPtr = activeBuffArray + buffOffset;
                for (var i = 0; i < 30; i++)
                {
                    var info = Marshal.PtrToStructure<BuffInfo>(buffPtr);
                    Log.Information($"Buff #{i,2} {info.buff} {info.duration:f}");
                    buffPtr += (sizeof(short) + sizeof(short) + sizeof(float) + sizeof(int));
                }


                lastDump = foundBuff;
            }
        }

        private bool SearchBuffArray(short needle, float min = 0, float max = 99999)
        {

            if (activeBuffArray == IntPtr.Zero) return false;

            var ptr = activeBuffArray + buffOffset;

            for (var i = 0; i < 60; i++)
            {
                var info = Marshal.PtrToStructure<BuffInfo>(ptr);
                if (info.buff == needle)
                {
                    var dur = Math.Abs(info.duration);
                    return dur >= min && dur <= max;
                }
                ptr += BuffInfoSize;
            }
            return false;
        }

        private void UpdateBuffAddress()
        {
            try
            {
                activeBuffArray = FindBuffAddress();
            }
            catch (Exception)
            {
                //Before you're loaded in
                activeBuffArray = IntPtr.Zero;
            }
        }

        private unsafe IntPtr FindBuffAddress()
        {
            var num = Marshal.ReadIntPtr(BuffVTableAddr);
            var step2 = (IntPtr) (Marshal.ReadInt64(num) + 0x270);
            var step3 = Marshal.ReadIntPtr(step2);
            var callback = Marshal.GetDelegateForFunctionPointer<getArray>(step3);
            return (IntPtr) callback((long*) num) + 8;
        }

        private void PopulateMyCustomDict()
        {
            // Paladin
            customIds.Add(PLD.HolySpirit);
            customIds.Add(PLD.HolyCircle);

            // Dancer
            customIds.Add(DNC.StandardStep);
            customIds.Add(DNC.TechnicalStep);
            customIds.Add(DNC.ReverseCascade);
            customIds.Add(DNC.Fountainfall);
            customIds.Add(DNC.RisingWindmill);
            customIds.Add(DNC.Bloodshower);
            customIds.Add(DNC.FanDance1);
            customIds.Add(DNC.FanDance2);
            // Monk
            customIds.Add(MNK.SnapPunch);
            customIds.Add(MNK.DragonKick);
            customIds.Add(MNK.Rockbreaker);
            customIds.Add(MNK.FistsOfFire);
            // Red Mage
            customIds.Add(RDM.Verstone);
            customIds.Add(RDM.Verfire);

        }

        private void PopulateDict()
        {
            PopulateMyCustomDict();

            customIds.Add(16477);
            customIds.Add(88);
            customIds.Add(84);
            customIds.Add(3632);
            customIds.Add(16468);
            customIds.Add(3538);
            customIds.Add(3539);
            customIds.Add(16457);
            customIds.Add(42);
            customIds.Add(45);
            customIds.Add(16462);
            customIds.Add(SAM.Yukikaze);
            customIds.Add(SAM.Gekko);
            customIds.Add(SAM.Kasha);
            customIds.Add(SAM.Mangetsu);
            customIds.Add(SAM.Oka);
            customIds.Add(3563);
            customIds.Add(2255);
            customIds.Add(16488);
            customIds.Add(GNB.SolidBarrel);
            customIds.Add(GNB.WickedTalon);
            customIds.Add(GNB.Continuation);
            customIds.Add(16149);
            customIds.Add(7413);
            customIds.Add(2870);
            customIds.Add(3575);
            customIds.Add(149);
            customIds.Add(17055);
            customIds.Add(3582);
            customIds.Add(3581);
            customIds.Add(163);
            customIds.Add(181);
            customIds.Add(3578);
            customIds.Add(16543);
            customIds.Add(167);
            customIds.Add(16531);
            customIds.Add(16534);
            customIds.Add(3559);
            customIds.Add(97);
            customIds.Add(RDM.Veraero2);
            customIds.Add(RDM.Verthunder2);
            customIds.Add(RDM.Redoublement);
            customIds.Add(3566);
            customIds.Add(92);
            customIds.Add(3553);
            customIds.Add(2873);
            customIds.Add(3579);
            customIds.Add(17209);
            customIds.Add(SAM.Seigan);
            customIds.Add(21);
            customIds.Add(DNC.Bloodshower);
            customIds.Add(DNC.RisingWindmill);
            customIds.Add(RDM.Verstone);
            customIds.Add(RDM.Verfire);
            customIds.Add(MNK.Rockbreaker);
            customIds.Add(BLM.LeyLines);
            customIds.Add(PLD.Requiescat);
            vanillaIds.Add(0x3e75);
            vanillaIds.Add(0x3e76);
            vanillaIds.Add(0x3e86);
            vanillaIds.Add(0x3f10);
            vanillaIds.Add(0x3f25);
            vanillaIds.Add(0x3f1c);
            vanillaIds.Add(0x3f1d);
            vanillaIds.Add(0x3f1e);
            vanillaIds.Add(0x451f);
            vanillaIds.Add(0x42ff);
            vanillaIds.Add(0x4300);
            vanillaIds.Add(0x49d4);
            vanillaIds.Add(0x49d5);
            vanillaIds.Add(0x49e9);
            vanillaIds.Add(0x49ea);
            vanillaIds.Add(0x49f4);
            vanillaIds.Add(0x49f7);
            vanillaIds.Add(0x49f9);
            vanillaIds.Add(0x4a06);
            vanillaIds.Add(0x4a31);
            vanillaIds.Add(0x4a32);
            vanillaIds.Add(0x4a35);
            vanillaIds.Add(0x4792);
            vanillaIds.Add(0x452f);
            vanillaIds.Add(0x453f);
            vanillaIds.Add(0x454c);
            vanillaIds.Add(0x455c);
            vanillaIds.Add(0x455d);
            vanillaIds.Add(0x4561);
            vanillaIds.Add(0x4565);
            vanillaIds.Add(0x4566);
            vanillaIds.Add(0x45a0);
            vanillaIds.Add(0x45c8);
            vanillaIds.Add(0x45c9);
            vanillaIds.Add(0x45cd);
            vanillaIds.Add(0x4197);
            vanillaIds.Add(0x4199);
            vanillaIds.Add(0x419b);
            vanillaIds.Add(0x419d);
            vanillaIds.Add(0x419f);
            vanillaIds.Add(0x4198);
            vanillaIds.Add(0x419a);
            vanillaIds.Add(0x419c);
            vanillaIds.Add(0x419e);
            vanillaIds.Add(0x41a0);
            vanillaIds.Add(0x41a1);
            vanillaIds.Add(0x41a2);
            vanillaIds.Add(0x41a3);
            vanillaIds.Add(0x417e);
            vanillaIds.Add(0x404f);
            vanillaIds.Add(0x4051);
            vanillaIds.Add(0x4052);
            vanillaIds.Add(0x4055);
            vanillaIds.Add(0x4053);
            vanillaIds.Add(0x4056);
            vanillaIds.Add(0x405e);
            vanillaIds.Add(0x405f);
            vanillaIds.Add(0x4063);
            vanillaIds.Add(0x406f);
            vanillaIds.Add(0x4074);
            vanillaIds.Add(0x4075);
            vanillaIds.Add(0x4076);
            vanillaIds.Add(0x407d);
            vanillaIds.Add(0x407f);
            vanillaIds.Add(0x4083);
            vanillaIds.Add(0x4080);
            vanillaIds.Add(0x4081);
            vanillaIds.Add(0x4082);
            vanillaIds.Add(0x4084);
            vanillaIds.Add(0x408e);
            vanillaIds.Add(0x4091);
            vanillaIds.Add(0x4092);
            vanillaIds.Add(0x4094);
            vanillaIds.Add(0x4095);
            vanillaIds.Add(0x409c);
            vanillaIds.Add(0x409d);
            vanillaIds.Add(0x40aa);
            vanillaIds.Add(0x40ab);
            vanillaIds.Add(0x40ad);
            vanillaIds.Add(0x40ae);
            vanillaIds.Add(0x272b);
            vanillaIds.Add(0x222a);
            vanillaIds.Add(0x222d);
            vanillaIds.Add(0x222e);
            vanillaIds.Add(0x223b);
            vanillaIds.Add(0x2265);
            vanillaIds.Add(0x2267);
            vanillaIds.Add(0x2268);
            vanillaIds.Add(0x2269);
            vanillaIds.Add(0x2274);
            vanillaIds.Add(0x2290);
            vanillaIds.Add(0x2291);
            vanillaIds.Add(0x2292);
            vanillaIds.Add(0x229c);
            vanillaIds.Add(0x229e);
            vanillaIds.Add(0x22a8);
            vanillaIds.Add(0x22b3);
            vanillaIds.Add(0x22b5);
            vanillaIds.Add(0x22b7);
            vanillaIds.Add(0x22d1);
            vanillaIds.Add(0x4575);
            vanillaIds.Add(0x2335);
            vanillaIds.Add(0x1ebb);
            vanillaIds.Add(0x1cdd);
            vanillaIds.Add(0x1cee);
            vanillaIds.Add(0x1cef);
            vanillaIds.Add(0x1cf1);
            vanillaIds.Add(0x1cf3);
            vanillaIds.Add(0x1cf4);
            vanillaIds.Add(0x1cf7);
            vanillaIds.Add(0x1cfc);
            vanillaIds.Add(0x1d17);
            vanillaIds.Add(0x1d00);
            vanillaIds.Add(0x1d01);
            vanillaIds.Add(0x1d05);
            vanillaIds.Add(0x1d07);
            vanillaIds.Add(0x1d0b);
            vanillaIds.Add(0x1d0d);
            vanillaIds.Add(0x1d0f);
            vanillaIds.Add(0x1d12);
            vanillaIds.Add(0x1d13);
            vanillaIds.Add(0x1d4f);
            vanillaIds.Add(0x1d64);
            vanillaIds.Add(0x1d50);
            vanillaIds.Add(0x1d58);
            vanillaIds.Add(0x1d59);
            vanillaIds.Add(0x1d51);
            vanillaIds.Add(0x1d53);
            vanillaIds.Add(0x1d66);
            vanillaIds.Add(0x1d55);
            vanillaIds.Add(0xdda);
            vanillaIds.Add(0xddd);
            vanillaIds.Add(0xdde);
            vanillaIds.Add(0xde3);
            vanillaIds.Add(0xdf0);
            vanillaIds.Add(0xe00);
            vanillaIds.Add(0xe0b);
            vanillaIds.Add(0xe0c);
            vanillaIds.Add(0xe0e);
            vanillaIds.Add(0xe0f);
            vanillaIds.Add(0xe11);
            vanillaIds.Add(0xe18);
            vanillaIds.Add(0xfed);
            vanillaIds.Add(0xff7);
            vanillaIds.Add(0xffb);
            vanillaIds.Add(0xfe9);
            vanillaIds.Add(0xb30);
            vanillaIds.Add(0x12e);
            vanillaIds.Add(0x8d3);
            vanillaIds.Add(0x8d4);
            vanillaIds.Add(0x8d5);
            vanillaIds.Add(0x8d7);
            vanillaIds.Add(0xb32);
            vanillaIds.Add(0xb34);
            vanillaIds.Add(0xb38);
            vanillaIds.Add(0xb3e);
            vanillaIds.Add(0x12d);
            vanillaIds.Add(0x26);
            vanillaIds.Add(0x31);
            vanillaIds.Add(0x33);
            vanillaIds.Add(0x4b);
            vanillaIds.Add(0x62);
            vanillaIds.Add(0x64);
            vanillaIds.Add(0x71);
            vanillaIds.Add(0x77);
            vanillaIds.Add(0x7f);
            vanillaIds.Add(0x79);
            vanillaIds.Add(0x84);
            vanillaIds.Add(0x90);
            vanillaIds.Add(0x99);
            vanillaIds.Add(0xa4);
            vanillaIds.Add(0xb2);
            vanillaIds.Add(0xa8);
            vanillaIds.Add(0xac);
            vanillaIds.Add(0xb8);
            vanillaIds.Add(0xe2);
            vanillaIds.Add(0x10f);
            vanillaIds.Add(0xf3);
            vanillaIds.Add(0x10e);
            vanillaIds.Add(0x110);
            vanillaIds.Add(0x111);

            // If an id exists as a custom id, it should not be in vanilla id
            vanillaIds.RemoveWhere(id => customIds.Contains(id));
        }
    }
}
