During development and testing, agents have standing permission to close the Big Walk game window or terminate its process at any time without asking the user.

Ramblers is a personal 0.x game mod under active development. Do not add speculative fail-closed gameplay gates. Trust Big Walk's stock physics and gameplay checks; when a heuristic is uncertain, log it and continue. Retain hard guards only for demonstrated native-crash paths, exact target/host-authority integrity, and capability or lifecycle ownership. Any new behavior-blocking guard requires exact runtime evidence from the failure it addresses.

Do not leave any comments or add any new documentation.

Do not edit README.md as part of a feature or fix. The only acceptable README change corrects a statement that is wrong, stale, or out of date, and it ships as its own change with the correction named in the commit message.
