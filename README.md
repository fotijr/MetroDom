# MetroDom [![Build Status](https://travis-ci.org/fotijr/MetroDom.svg?branch=master)](https://travis-ci.org/fotijr/MetroDom)
MetroDom is a hardware and software project to program and control physical instruments.

## Solution Architecture
MetroDom is a .NET solution with multiple projects:
 - [MetroDom.Core](/tree/master/MetroDom.Core): A class library project. Contains logic and interfaces for controlling instruments.
 - [Conductor](./tree/master/MetroDom.Conductor): A Windows Form app powered by MetroDom.Core. Includes UI controls for instruments and some editing tools for MIDI files.
  - [Instruments](../tree/master/Instruments): Documentation on hardware and code that runs on the hardware. Includes an implementation of Core interface for interoperability.
	
## Roadmap
 - Control hardware with a C# brain (MetroDom.Conductor)
 - Interface with Arduinos, which play physical instruments.
    - Communicate via serial over USB
    - Ideally using MIDI protocol