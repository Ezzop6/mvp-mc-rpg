# CLAUDE.md

## Language

- All code and code comments in this repository are written in English.
- Documentation may be written in the language the user speaks with you.
- Talk to the user in the language they chose.

## Comments

- A comment is at most one line per function.
- If you need more than one line to explain something, the code probably doesn't make sense as written. Rewrite the code instead.
- Anything that needs real explanation goes into the documentation, kept to the necessary minimum.

## TODO.md

- `TODO.md` (repo root) is the task list. Only the main session edits it; subagents never touch it.

## Delegation

- Delegate all work to subagents whenever possible.
- Personally supervise only the operation and the merging of content.
- Subagents may work in separate worktrees.
- Brief every subagent with: the goal, the relevant context, a definition of done, and the files it may touch.

## Ambiguity

- If a request is unclear and a wrong guess would be expensive, ask the user.
- Otherwise pick a reasonable option and state which one you chose.
