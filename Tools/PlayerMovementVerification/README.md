# Player movement verification tools

Headless tooling for `Assets/Scripts/Player/ThirdPersonPlayerController.cs`. The default maximum
speed is now the Hero_Walk reference pace (1.25 m/s); acceleration, turning, camera-relative input
and the rest of the movement mechanics are unchanged.

* `simulate_player_movement.py` — mirrors the controller's `Update()` loop statement by statement,
  together with the `CharacterController` behaviour it depends on (skin-width collision volume,
  slide-along-surface, depenetration, ground snapping, step offset). Arena geometry is parsed from
  `Assets/Scenes/CombatTestScene.unity`, so the slopes, platforms and walls under test are the real
  ones. Every scenario corresponds to a check in the in-Editor suite
  (`Assets/Scripts/Player/PlayerControllerTest.cs`), including the regression check for Unity's
  `IsNormalized(dir, 0.001f)` assertion: a zero-length motion must never reach
  `CharacterController.Move` (the guard in the controller skips those frames instead).

```bash
python3 Tools/PlayerMovementVerification/simulate_player_movement.py
```

Exit code 0 means every check passed. See `REPORT.md` for the recorded results and for the issues
that were found and fixed.

This folder is deliberately outside `Assets/`: Unity does not import it, and it is not part of any
build. The authoritative test for the project is the Play Mode suite
`PlayerControllerTest`, which drives the real component in the Editor.
