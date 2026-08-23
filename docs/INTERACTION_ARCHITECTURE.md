# Interaction architecture

Ramblers treats an interaction as a transaction over one exact Big Walk object. The language model chooses intent and an opaque object ID; deterministic C# owns object identity, movement, authority, native command dispatch, and confirmation.

This distinction matters because a `CastableTarget` answers two different questions:

1. Which exact world object did the player indicate or which object is nearby?
2. What primary action may this companion perform on that object in its current position and hands state?

The first answer must survive long enough for the companion to walk, pick something up, or complete an earlier action. The second answer must remain dynamic because Big Walk legitimately changes it with reach, pose, held items, keys, block state, and puzzle state. Earlier implementations combined those questions. A far button could therefore disappear before approach, and an empty item holder could disappear before a preceding pickup made placement possible.

## End-to-end flow

```text
speech stops
    |
    v
utterance snapshot
  - exact gaze target
  - exact held item
  - bounded nearby/recent entities
    |
    v
private model context                   model selects intent + opaque ID
  - natural names                                  |
  - stable interaction IDs                         v
    +--------------------------------------> strict tool router
                                                    |
                                                    v
                                          exact turn-map lookup
                                                    |
                                                    v
                                         typed affordance driver
                                                    |
                                                    v
                                     readiness -> shared approach
                                                    |
                                                    v
                                      revalidate -> native authority
                                                    |
                                                    v
                                         exact state confirmation
```

No stage resolves an object by display name, screen position, or nearest-neighbour search. Names help the model choose among IDs; only the frozen object and network identities reach action code.

## 1. Utterance snapshot

`OpenAIRealtimeBridge` captures all physical context when human speech ends and binds it to one response turn. A new utterance invalidates undispatched references. Multiple tool calls from one response execute in order, and only a verified job completion can advance transient turn state.

For example, after `pick_up_item` confirms that the companion holds the same prop, the turn snapshot may expose held-item use and placement for that exact prop. It does not rescan the camera or replace the prop with a nearby object.

## 2. Exact references and bounded discovery

`CompanionInteractionReference` stores the original `CastableTarget`, its Unity instance ID, and its ancestor `NetworkIdentity` when present. Its model-facing ID is either:

- `interaction:net:<net-id>:castable:<instance-id>`
- `interaction:local:<instance-id>`

`CompanionInteractableDiscovery` searches only bounded, game-owned sources: nearby `NetworkServer.spawned` roots, `PropHome.allPropHomes`, `Prop.allProps`, and the exact gaze observation already captured for the turn. It has explicit limits for roots, hierarchy nodes, candidates, remembered entries, distance, age, and output count. It does not use `Resources.FindObjectsOfTypeAll` or a whole-scene fallback.

Reference capture is structural and prerequisite-free. It records that the exact object contains a supported native primitive without requiring the companion to be close enough, already holding an item, or already holding a key. This lets one response compose actions such as “pick up the light and put it back on that stand.”

## 3. Strict resolution and verified state advancement

`CompanionEntityReferenceSet` is the only map from an interaction ID back to a world object. Resolution revalidates the original managed component, instance ID, network component, and network ID. It never substitutes another target.

The initial turn state is frozen. It can change only through an explicit `CompanionTurnHandsTransition` emitted after a physical job confirms its native postcondition:

- `HoldingExactProp`
- `HandsEmpty`
- `HoldingExactPlayer`

That transition rebuilds only capabilities affected by the verified state change. The spoken world identities remain unchanged.

## 4. Typed native affordance drivers

`CompanionAffordanceTarget` selects one driver using Big Walk's own `CastableOutcome` precedence. The supported primitive drivers are:

- world `PeckSwitch`
- held-item primary switch
- `PlayerPose`, including explicit sitting
- `PropHome` placement

Each `ICompanionAffordanceDriver` owns the same lifecycle contract:

1. Revalidate exact components and actor binding.
2. Return `NeedsApproach`, `Ready`, or `Unavailable`.
3. Build a typed activation plan only when ready.
4. Cross host authority through the corresponding stock command body.
5. Confirm the exact authoritative postcondition.

Reach is evaluated before actor-dependent outcome admission. A structurally known far target can request approach only while the companion owns its locomotion. While a human is carrying the companion, a world interaction is in-place: directional reach may make it ready, but it can never return `NeedsApproach` or fall through to an ordinary locomoting driver. For switches, Big Walk's fresh exact-point raycast and safety rules are then revalidated after alignment and immediately before authority.

Unsupported prerequisite combinations are rejected consistently at the central discriminator. They are not accepted merely because the target happened to be near when captured.

## 5. Shared approach, action-owned semantics

`CompanionApproachController` owns movement cadence, steering, progress observation, and action-scoped traversal recovery. Pickup, kick, player carry, directed movement, and interaction all use it.

The controller does not decide what “success” means. Each job still owns its native reach check, alignment, activation, confirmation, error codes, and structured logs. This keeps walking policy shared without making interactions pretend to be pickups or kicks.

## 6. Authority is a staged transaction

Native actions may have more than one authoritative phase. A keyed switch can apply a held-key effect before pressing the target switch; entering a sittable pose can precede the sitting command; a momentary switch has down and release phases.

Activation records authority before each native command and progresses phase-by-phase from exact receipts. Once any phase crosses authority, failure, timeout, or cancellation enters reconciliation instead of pretending nothing happened. This prevents a consumed key, entered pose, or pressed control from becoming orphaned state.

## 7. Capability ownership

The job coordinator arbitrates `Locomotion`, `Gaze`, and `Hands`. The interaction job claims locomotion only when its initial readiness requires approach, while retaining gaze and hands through activation and confirmation. Persistent follow intent remains separate; jobs temporarily own locomotion without erasing the player's follow preference.

Completion is also a capability boundary. A failure, cancellation, or timeout is not exposed to the model while its job is still active or holding resources. The controller retains the original operation token through compensation and only advances a tool batch after the job is settled. Successful pickup/carry holds and inspection presentation are narrow explicit exceptions: pickup/carry is synchronously concluded before the next call, while inspection retains gaze until assistant audio begins or the response is otherwise released.

Timeout begins cancellation; it is not itself completion. If native compensation cannot be observed within the bounded settlement window, Ramblers explicitly abandons only its own job ownership, leaves the actual hands/carry state to Big Walk, suppresses any inferred hands transition, and makes the next turn snapshot authoritative. Runtime telemetry distinguishes `RECONCILED`, `RECONCILIATION_ABANDONED`, and `RECONCILIATION_DETACHED` instead of calling all three success.

An image presentation must be the terminal tool in its response. If a model nevertheless emits unseen later calls in the same batch, those calls are not run; the image is delivered first so the next action can be chosen from the observation while the companion retains its look direction.

## Adding a new interaction primitive

A genuinely new Big Walk primitive should require one vertical extension rather than a puzzle-name branch:

1. Add a structural kind used only for context description.
2. Add an `ICompanionAffordanceDriver` for the native component.
3. Extend the central outcome discriminator using Big Walk's native precedence.
4. Define exact identity and actor validation.
5. Define readiness and any supported prerequisites.
6. Dispatch the stock authoritative command path.
7. Confirm the exact replicated/native postcondition.
8. Add a pure protocol probe and an executable lifecycle probe.
9. Add runtime audit invariants for start, authority, confirmation, and failure.

Scene names, puzzle names, and display names must not appear in production action selection.

## Evidence boundaries

- A compiler success proves the source is type-correct against the installed interop assemblies.
- Protocol and executable probes prove deterministic policy and lifecycle composition outside the running game.
- Matching build/deployment hashes prove which candidate is installed.
- Structured in-process logs prove which runtime paths executed.
- Visual QA proves that Big Walk presented the intended animation, movement, and puzzle result.

The first three are automated before handoff. They do not replace the final visual QA pass.
