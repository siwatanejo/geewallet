#!/usr/bin/env bash
set -euxo pipefail

# this is the equivalent of using the 'build-packages' (not stage-packages) section in snapcraft
# but as we're not using the 'make' plugin, we need to this manually now
DEBIAN_FRONTEND=noninteractive sudo apt install -y fsharp build-essential pkg-config cli-common-dev mono-devel


./configure.sh --prefix=./staging
make
dotnet publish -r linux-x64 -o ./src/GWallet.Frontend.Console/bin/Release ./src/GWallet.Frontend.Console/GWallet.Frontend.Console.fsproj
make install

#this below is to prevent the possible error "Failed to reuse files from previous run: The 'pull' step of 'gwallet' is out of date: The source has changed on disk."
#snapcraft clean gwallet -s pull

snapcraft --destructive-mode
