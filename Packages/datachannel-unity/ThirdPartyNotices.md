# Third-party notices

When this package ships prebuilt plugins under `Plugins/`, the following apply.

## libdatachannel

- Project: https://github.com/paullouisageneau/libdatachannel
- License: MPL-2.0
- Pin: see `native/versions.lock` (`libdatachannel=v0.24.5`)
- Source offer: https://github.com/paullouisageneau/libdatachannel/tree/v0.24.5

## datachannel-wasm (WebGL)

- Project: https://github.com/paullouisageneau/datachannel-wasm
- License: MIT
- Pin: `datachannel-wasm=v0.4.0`
- Source: https://github.com/paullouisageneau/datachannel-wasm/tree/v0.4.0

## Transitive (via libdatachannel tree)

Typically includes libjuice (MPL-2.0), usrsctp, and MbedTLS. Exact versions follow the pinned libdatachannel tag’s submodules. Provide matching source URLs for any Modifications you distribute.

## Package-owned code

C# (`DataChannelUnity`) and the `dcu_*` wrapper sources in this repository are under `LICENSE.md` (MIT) unless a file header says otherwise.
