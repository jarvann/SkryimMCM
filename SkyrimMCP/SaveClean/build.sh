#!/usr/bin/env bash
# Compiles SaveClean against an installed ReSaver (Java 8 bytecode, so ReSaver's bundled JRE can run it). The .class is committed
# and shipped with the server (SkyLinkAI_Server\SaveClean\), so Windows builds don't need a JDK.
set -euo pipefail
RS=${RESAVER_DIR:-/mnt/c/Games/Modding/Resaver}
DIR=$(dirname "$(readlink -f "$0")")
CP="$RS/target/ReSaver.jar:$(ls "$RS"/target/lib/*.jar | tr '\n' ':')"
javac --release 8 -nowarn -cp "$CP" -d "$DIR" "$DIR/SaveClean.java"
echo "built $DIR/SaveClean.class"
