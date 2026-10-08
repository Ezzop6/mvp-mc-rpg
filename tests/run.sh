#!/usr/bin/env bash
# CPU test projects, --gpu[=list or bookmark] also the bookmark lists in the game (docs/testing.md).
cd "$(dirname "$0")/.." || exit 1
status=0
for t in WorldData World Models Import; do
	echo "== $t"
	dotnet run --project "tests/$t" || status=1
done
for a in "$@"; do
	case $a in
	--gpu | --gpu=*)
		echo "== GPU"
		filter=${a#--gpu}
		"${GODOT:-godot-mono}" --path . res://core/scenes/main/main.tscn -- "--test$filter" || status=1
		;;
	esac
done
exit $status
