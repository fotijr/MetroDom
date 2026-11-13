"""Tests for check_hardware.check_body: python3 -m unittest discover -s scripts"""
from __future__ import annotations

import copy
import json
import tomllib
import unittest
from pathlib import Path

import check_hardware

ROOT = Path(__file__).resolve().parent.parent

MODULES = {
    "brain-pico": {"job": "brain"},
    "body-backplane": {"job": "body logic"},
    "driver-mosfet-8ch": {"job": "drive", "outputs": 8},
    "striker-solenoid": {"job": "strike"},
    "damper-solenoid": {"job": "damp"},
    "led-accent": {"job": "light"},
}


def instrument(strikers: int = 3, drivers: int = 1, extra: tuple[str, ...] = ()) -> dict:
    mods = [{"id": "brain-pico", "qty": 1}, {"id": "body-backplane", "qty": 1},
            {"id": "driver-mosfet-8ch", "qty": drivers}, {"id": "striker-solenoid", "qty": strikers}]
    return {"id": "snare", "modules": mods + [{"id": e, "qty": 1} for e in extra]}


def body(notes: int = 3, driver: str = "pca9685", **extra) -> dict:
    return {"id": "snare", "driver": {"type": driver},
            "notes": [{"note": 60 + i, "output": i} for i in range(notes)], **extra}


class CheckBodyTests(unittest.TestCase):
    def check(self, inst: dict, b: dict) -> list[str]:
        return check_hardware.check_body("body.json", inst, b, MODULES)

    def test_a_body_that_fits_passes(self):
        self.assertEqual([], self.check(instrument(), body()))

    def test_the_template_passes(self):
        tpl = ROOT / "Instruments" / "_template"
        inst = tomllib.loads((tpl / "instrument.toml").read_text(encoding="utf-8"))
        b = json.loads((tpl / "body.json").read_text(encoding="utf-8"))
        self.assertEqual([], self.check(inst, b))

    def test_the_id_must_match(self):
        self.assertIn("must equal the instrument id", self.check(instrument(), body(id="other"))[0])

    def test_a_body_needs_a_backplane(self):
        inst = instrument()
        inst["modules"] = [m for m in inst["modules"] if m["id"] != "body-backplane"]
        self.assertIn("body-backplane", self.check(inst, body())[0])

    def test_more_notes_than_driver_outputs_fails(self):
        errors = self.check(instrument(strikers=9), body(notes=9))
        self.assertTrue(any("switch 8 output(s)" in e for e in errors), errors)
        self.assertEqual([], self.check(instrument(strikers=9, drivers=2), body(notes=9)))

    def test_a_pca9685_output_past_the_drivers_fails(self):
        b = body(notes=2)
        b["notes"][1]["output"] = 12
        self.assertIn("output 12 has no driver channel", self.check(instrument(), b)[0])

    def test_a_gpio_body_uses_pins_not_driver_channels(self):
        b = body(notes=2, driver="gpio")
        b["notes"][1]["output"] = 15
        self.assertEqual([], self.check(instrument(), b))

    def test_every_note_needs_an_actuator_and_dampers_count(self):
        self.assertIn("actuator module", self.check(instrument(strikers=2), body(notes=3))[0])
        self.assertEqual([], self.check(instrument(strikers=2, extra=("damper-solenoid",)), body(notes=3)))

    def test_accent_leds_need_the_led_module(self):
        b = body(accent={"leds": 9})
        self.assertIn("led-accent", self.check(instrument(), b)[0])
        self.assertEqual([], self.check(instrument(extra=("led-accent",)), copy.deepcopy(b)))

    def test_the_repo_passes(self):
        self.assertEqual(0, check_hardware.main())


if __name__ == "__main__":
    unittest.main()
