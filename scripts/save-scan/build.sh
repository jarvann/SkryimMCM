#!/usr/bin/env bash
# Compiles SaveScan against the ReSaver install (Java 8 bytecode, so ReSaver's bundled JRE can run it).
set -euo pipefail
RS=${RESAVER_DIR:-/mnt/c/Games/Modding/Resaver}
DIR=$(dirname "$(readlink -f "$0")")
CP="$RS/target/ReSaver.jar:$(ls "$RS"/target/lib/*.jar | tr '\n' ':')"
javac --release 8 -nowarn -cp "$CP" -d "$DIR/out" "$DIR/SaveScan.java"
echo "built $DIR/out"
