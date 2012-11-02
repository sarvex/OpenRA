#region Copyright & License Information
/*
 * Copyright 2007-2012 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Mods.RA.Activities;
using OpenRA.Mods.RA.Air;
using OpenRA.Mods.RA.Buildings;
using OpenRA.Mods.RA.Move;
using OpenRA.Network;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.RA.Missions
{
	class Allies03ScriptInfo : TraitInfo<Allies03Script>, Requires<SpawnMapActorsInfo> { }

	class Allies03Script : IHasObjectives, IWorldLoaded, ITick
	{
		public event ObjectivesUpdatedEventHandler OnObjectivesUpdated = notify => { };

		public IEnumerable<Objective> Objectives { get { return objectives.Values; } }

		Dictionary<int, Objective> objectives = new Dictionary<int, Objective>
		{
			{ EvacuateID, new Objective(ObjectiveType.Primary, "Following the rescue of Einstein, the Allies are now being flanked from both sides. Evacuate {0} units before the remaining Allied forces in the area are wiped out.".F(UnitsEvacuatedThreshold), ObjectiveStatus.InProgress) },
		};

		const int EvacuateID = 0;
		const int UnitsEvacuatedThreshold = 100;
		int unitsEvacuated;
		InfoWidget evacuateWidget;
		const string ShortEvacuateTemplate = "{0}/{1} units evacuated";

		World world;
		Player allies1;
		Player allies2;
		Player evacuees;
		Player soviets;

		Actor exit1TopLeft;
		Actor exit1TopRight;
		Actor exit1BottomLeft;
		Actor exit1BottomRight;
		Actor exit1ExitPoint;

		Actor exit2TopLeft;
		Actor exit2TopRight;
		Actor exit2BottomLeft;
		Actor exit2BottomRight;
		Actor exit2ExitPoint;

		Actor sovietEntryPoint1;
		Actor sovietEntryPoint2;
		Actor sovietEntryPoint3;
		Actor sovietEntryPoint4;
		CPos[] sovietEntryPoints;
		Actor sovietRallyPoint1;
		Actor sovietRallyPoint2;
		Actor sovietRallyPoint3;
		Actor sovietRallyPoint4;
		CPos[] sovietRallyPoints;

		static readonly string[] SovietVehicles = { "3tnk", "3tnk", "3tnk", "v2rl", "ftrk", "apc" };
		static readonly string[] SovietInfantry = { "e1", "e1", "e1", "e1", "e2", "e2", "e3", "e3", "e4", "e4" };

		int attackAtFrame = 250;
		int attackAtFrameIncrement = 250;

		Actor allies1EntryPoint;
		Actor allies1MovePoint;

		Actor allies2EntryPoint;
		Actor allies2MovePoint;

		const string McvName = "mcv";

		void MissionFailed(string text)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			allies1.WinState = allies2.WinState = WinState.Lost;
			foreach (var actor in world.Actors.Where(a => a.IsInWorld && (a.Owner == allies1 || a.Owner == allies2) && !a.IsDead()))
			{
				actor.Kill(actor);
			}
			Game.AddChatLine(Color.Red, "Mission failed", text);
			Sound.Play("misnlst1.aud");
		}

		void MissionAccomplished(string text)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			allies1.WinState = allies2.WinState = WinState.Won;
			Game.AddChatLine(Color.Blue, "Mission accomplished", text);
			Sound.Play("misnwon1.aud");
		}

		public void Tick(Actor self)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			if (world.FrameNumber == 1)
			{
				SpawnAlliedUnits();
				evacuateWidget = new InfoWidget("", new float2(Game.viewport.Width * 0.35f, Game.viewport.Height * 0.9f));
				Ui.Root.AddChild(evacuateWidget);
				UpdateUnitsEvacuated();
			}
			if (world.FrameNumber == attackAtFrame)
			{
				SpawnSovietUnits();
				attackAtFrame += attackAtFrameIncrement;
				attackAtFrameIncrement = Math.Max(attackAtFrameIncrement - 5, 100);
				Game.Debug(attackAtFrame.ToString());
				Game.Debug(attackAtFrameIncrement.ToString());
			}
			ManageSovietUnits();
			EvacuateAlliedUnits(exit1TopLeft.CenterLocation, exit1BottomRight.CenterLocation, exit1ExitPoint.Location);
			EvacuateAlliedUnits(exit2TopLeft.CenterLocation, exit2BottomRight.CenterLocation, exit2ExitPoint.Location);
			if (!world.Actors.Any(a => (a.Owner == allies1 || a.Owner == allies2) && a.IsInWorld && !a.IsDead() && ((a.HasTrait<Building>() && !a.HasTrait<Wall>()) || a.HasTrait<BaseBuilding>())))
			{
				MissionFailed("The remaining Allied forces in the area have been wiped out.");
			}
		}

		void SpawnSovietUnits()
		{
			var route = world.SharedRandom.Next(sovietEntryPoints.Length);
			var spawnPoint = sovietEntryPoints[route];
			var rallyPoint = sovietRallyPoints[route];
			var unit = world.CreateActor(SovietVehicles.Concat(SovietInfantry).Random(world.SharedRandom), new TypeDictionary { new LocationInit(spawnPoint), new OwnerInit(soviets) });
			unit.QueueActivity(new AttackMove.AttackMoveActivity(unit, new Move.Move(rallyPoint, 5)));
		}

		void ManageSovietUnits()
		{
			var units = world.Actors.Where(u => u.IsInWorld && !u.IsDead() && u.IsIdle && u.HasTrait<Mobile>() && u.Owner == soviets);
			foreach (var unit in units)
			{
				var enemyUnits = world.FindAliveCombatantActorsInCircle(unit.CenterLocation, 100)
					.Where(u => (u.Owner == allies1 || u.Owner == allies2) && ((u.HasTrait<Building>() && !u.HasTrait<Wall>()) || u.HasTrait<Mobile>()));
				var targetEnemyUnit = enemyUnits.OrderBy(u => (unit.CenterLocation - u.CenterLocation).LengthSquared).FirstOrDefault();
				if (targetEnemyUnit != null)
				{
					unit.QueueActivity(new AttackMove.AttackMoveActivity(unit, new Move.Move(targetEnemyUnit.Location, 5)));
				}
			}
		}

		void SpawnAlliedUnits()
		{
			var unit = world.CreateActor(McvName, new TypeDictionary
			{
				new LocationInit(allies1EntryPoint.Location), 
				new OwnerInit(allies1),
				new FacingInit(Util.GetFacing(allies1MovePoint.Location - allies1EntryPoint.Location, 0)) 
			});
			unit.QueueActivity(new Move.Move(allies1MovePoint.Location));
			unit = world.CreateActor(McvName, new TypeDictionary 
			{ 
				new LocationInit(allies2EntryPoint.Location), 
				new OwnerInit(allies2),
				new FacingInit(Util.GetFacing(allies2MovePoint.Location - allies2EntryPoint.Location, 0))
			});
			unit.QueueActivity(new Move.Move(allies2MovePoint.Location));
		}

		void UpdateUnitsEvacuated()
		{
			evacuateWidget.Text = ShortEvacuateTemplate.F(unitsEvacuated, UnitsEvacuatedThreshold);
			if (objectives[EvacuateID].Status == ObjectiveStatus.InProgress && unitsEvacuated >= UnitsEvacuatedThreshold)
			{
				objectives[EvacuateID].Status = ObjectiveStatus.Completed;
				OnObjectivesUpdated(true);
				MissionAccomplished("The remaining Allied forces in the area have evacuated.");
			}
		}

		void EvacuateAlliedUnits(PPos a, PPos b, CPos exit)
		{
			var units = world.FindAliveCombatantActorsInBox(a, b)
				.Where(u => u.HasTrait<Mobile>() && !u.HasTrait<Aircraft>() && (u.Owner == allies1 || u.Owner == allies2));
			foreach (var unit in units)
			{
				unit.CancelActivity();
				unit.ChangeOwner(evacuees);
				unit.QueueActivity(new Move.Move(exit));
				unit.QueueActivity(new CallFunc(() => { unitsEvacuated++; UpdateUnitsEvacuated(); }));
				unit.QueueActivity(new RemoveSelf());
			}
		}

		public void WorldLoaded(World w)
		{
			world = w;
			allies1 = w.Players.Single(p => p.InternalName == "Allies1");
			allies2 = w.Players.Single(p => p.InternalName == "Allies2");
			evacuees = w.Players.Single(p => p.InternalName == "Evacuees");
			soviets = w.Players.Single(p => p.InternalName == "Soviets");
			var actors = w.WorldActor.Trait<SpawnMapActors>().Actors;
			exit1TopLeft = actors["Exit1TopLeft"];
			exit1TopRight = actors["Exit1TopRight"];
			exit1BottomLeft = actors["Exit1BottomLeft"];
			exit1BottomRight = actors["Exit1BottomRight"];
			exit1ExitPoint = actors["Exit1ExitPoint"];
			exit2TopLeft = actors["Exit2TopLeft"];
			exit2TopRight = actors["Exit2TopRight"];
			exit2BottomLeft = actors["Exit2BottomLeft"];
			exit2BottomRight = actors["Exit2BottomRight"];
			exit2ExitPoint = actors["Exit2ExitPoint"];
			allies1EntryPoint = actors["Allies1EntryPoint"];
			allies1MovePoint = actors["Allies1MovePoint"];
			allies2EntryPoint = actors["Allies2EntryPoint"];
			allies2MovePoint = actors["Allies2MovePoint"];
			sovietEntryPoint1 = actors["SovietEntryPoint1"];
			sovietEntryPoint2 = actors["SovietEntryPoint2"];
			sovietEntryPoint3 = actors["SovietEntryPoint3"];
			sovietEntryPoint4 = actors["SovietEntryPoint4"];
			sovietEntryPoints = new[] { sovietEntryPoint1, sovietEntryPoint2, sovietEntryPoint3, sovietEntryPoint4 }.Select(p => p.Location).ToArray();
			sovietRallyPoint1 = actors["SovietRallyPoint1"];
			sovietRallyPoint2 = actors["SovietRallyPoint2"];
			sovietRallyPoint3 = actors["SovietRallyPoint3"];
			sovietRallyPoint4 = actors["SovietRallyPoint4"];
			sovietRallyPoints = new[] { sovietRallyPoint1, sovietRallyPoint2, sovietRallyPoint3, sovietRallyPoint4 }.Select(p => p.Location).ToArray();
			if (w.LocalPlayer == null || w.LocalPlayer == allies1)
			{
				Game.MoveViewport(allies1EntryPoint.Location.ToFloat2());
			}
			else
			{
				Game.MoveViewport(allies2EntryPoint.Location.ToFloat2());
			}
			PlayMusic();
			OnObjectivesUpdated(false);
			Game.ConnectionStateChanged += StopMusic;
		}

		void PlayMusic()
		{
			if (!Rules.InstalledMusic.Any())
			{
				return;
			}
			var track = Rules.InstalledMusic.Random(Game.CosmeticRandom);
			Sound.PlayMusicThen(track.Value, PlayMusic);
		}

		void StopMusic(OrderManager orderManager)
		{
			if (!orderManager.GameStarted)
			{
				Sound.StopMusic();
				Game.ConnectionStateChanged -= StopMusic;
			}
		}
	}
}