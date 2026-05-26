# NATO C2 Doctrine — Project Reference

> Authoritative definitions the rest of this repo's UX copy, code comments, and
> JSON schema should align to. When you write a tooltip, name a class, or
> document a feature, prefer the terms in this file over inventing new ones.

## What "C2" means

In NATO military doctrine, **C2 stands for Command and Control**. It is the
foundational military function through which designated commanders exercise
authority and direction over assigned forces to accomplish a mission.

C2 splits into two distinct but deeply interrelated concepts:

- **Command** — the legal authority, responsibility, and leadership vested in
  an individual to organize, direct, and coordinate military forces.
- **Control** — the structures, systems, processes, and feedback loops a
  commander uses to monitor forces, manage resources, and adjust actions in
  real time.

The project's name, `NATO C2 RTS Hybrid`, refers to the technical-infrastructure
layer that supports both: the communication networks, software stacks, and
cloud-fed data flows that maintain situational awareness on the battlefield.

## How NATO implements C2

- **C3 Framework** — NATO often embeds C2 inside a broader political-military
  concept: **C3 (Consultation, Command, Control)**. "Consultation" ensures
  political consensus among allies; C2 carries out the resulting military
  decisions. If we ever add a multi-faction coordination layer to the demo,
  it should be called the *consultation* layer.

- **Centralized Command, Distributed Control** — NATO doctrine relies on
  centralized planning at higher headquarters (Allied Command Operations,
  ACO) but distributes tactical control and execution to local commanders for
  flexibility. The Mythos AIAutonomousMode is our codebase's analogue:
  centralized policy ("when to engage") with delegated execution ("how to
  maneuver"). UI affordances should make this hierarchy legible.

- **Technical Infrastructure** — the C2 *system* (lowercase-s) is the wire:
  communication networks, software stacks, data feeds. Our Link 16 simulator,
  STANAG 5066 ARQ layer, TAK CoT bridge, and federation dashboard are the
  fictionalised version of this layer.

## Common variations of the term

These names appear in real NATO documentation and should be used in tooltips,
class names, and architectural comments where appropriate. Don't coin new
acronyms when one of these fits.

| Acronym  | Expansion | When to use |
|----------|-----------|-------------|
| **C2**     | Command and Control | Default — the broad concept |
| **C2IS**   | Command and Control Information Systems | The software/UI layer (this repo) |
| **C4ISR**  | Command, Control, Communications, Computers, Intelligence, Surveillance, Reconnaissance | When sensor + comms + decision are all in play (e.g. drone PIP + radar + intent parser) |
| **MDC2**   | Multi-Domain Command and Control | When the scenario spans land + sea + air + space + cyber |
| **CJADC2** | Combined Joint All-Domain Command and Control | The US/coalition specialisation of MDC2 |

## Mapping to the codebase

| Doctrine concept | Codebase analogue |
|------------------|-------------------|
| Command authority | `NATO_C2_Manager.IssueCommand` — the operator's orders flow through here |
| Distributed control | `Agent` MonoBehaviours executing their own path-following + ORCA local avoidance |
| Consultation / C3 | (Not yet modeled — would be a coalition-vote layer above the manager) |
| C2IS — the wire | `FeedHub`, `Link16TdmaSimulator`, `Stanag5066*`, TAK CoT bridge |
| C4ISR — sensors + comms + decision | Drone PIP + Maven brackets + IntentParser + federation dashboard, taken together |
| Multi-Domain (MDC2) | `AltitudeLayer` (Ground / Low / High / Space) + the layer-aware ORCA filter |

## Sources

- [Wikipedia — Command and control](https://en.wikipedia.org/wiki/Command_and_control)
- [NDU — *Command and Control: Definitions and Implications*](https://digitalcommons.ndu.edu/defense-horizons/57/)
- [NATO Allied Command Operations (ACO)](https://www.nato.int/en/about-us/organization/nato-structure/allied-command-operations-aco)
- [NATO STO — Standardisation for C2 Simulation Interoperation](https://www.sto.nato.int/document/standardisation-for-c2-simulation-interoperation/)
- [NATO COBP (Code of Best Practice) for C2](http://www.dodccrp.org/events/7th_ICCRTS/NATO%20COBP.pdf)
- [C2 Centre of Excellence (C2COE) — Doctrine, Operations, Architecture](https://c2coe.org/education-training/doa/)
- [JAPCC — Evolving C2 for Decisive Air Power](https://www.japcc.org/articles/evolving-c2-for-decisive-air-power/)
- [Corvus Intelligence — Complete Guide to C2 Systems](https://corvusintell.com/blog/c2-systems/complete-guide-to-c2-systems/)
- [C4I Communication — What does Command and Control mean in military systems](https://c4icommunication.com/what-does-command-and-control-mean-in-military-system/)

## Open extensions worth considering

If we want to deepen the doctrinal fidelity, the highest-leverage additions
would be:

1. **A consultation layer** above `NATO_C2_Manager` for multi-operator scenarios — encodes the C in C3.
2. **An ACO-style HQ panel** that makes the centralized/distributed split visible: high-level intent at the top, distributed execution at the bottom.
3. **C4ISR overlay** that fuses sensor + comms + intent into one situational-awareness layer in the HUD (we have the pieces; they're not labelled as C4ISR yet).
4. **MDC2 mode** that explicitly shows the multi-domain (ground / air / space / cyber) cross-cuts — surface `AltitudeLayer` as a doctrine-level concept, not just a sim parameter.
