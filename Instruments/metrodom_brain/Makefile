# The brain firmware: build, flash and test (plan 0006 N5).
#   make build              compile for a Pico (BOARD=pico2 for a Pico 2) with the pinned
#                           core and libraries in sketch.yaml; the first run downloads them
#   make flash              build, then copy the UF2 to a Pico held in BOOTSEL
#   make test               host tests of the platform-free core (any C++17 compiler)
# Needs arduino-cli on PATH, or the one inside the Arduino IDE (found on macOS).
BOARD ?= pico
IDE_CLI := /Applications/Arduino IDE.app/Contents/Resources/app/lib/backend/resources/arduino-cli
ARDUINO_CLI ?= $(shell command -v arduino-cli 2>/dev/null || { test -x "$(IDE_CLI)" && echo "$(IDE_CLI)"; })
OUT := build/$(BOARD)
UF2 := $(OUT)/metrodom_brain.ino.uf2

.PHONY: build flash test clean check-cli

build: check-cli
	"$(ARDUINO_CLI)" compile --profile $(BOARD) --output-dir $(OUT) --warnings default .
	@echo "Built $(UF2)"

flash: build
	./flash.sh $(UF2)

test:
	$(MAKE) -C test

clean:
	rm -rf build
	$(MAKE) -C test clean

check-cli:
	@test -n "$(ARDUINO_CLI)" || { echo "arduino-cli not found: install it (brew install arduino-cli, or https://arduino.github.io/arduino-cli/) or the Arduino IDE, or pass ARDUINO_CLI=/path/to/arduino-cli"; exit 1; }
