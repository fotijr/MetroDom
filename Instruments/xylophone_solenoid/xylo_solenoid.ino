int firstSolenoid = 9;
int secondSolenoid = 10;
int thirdSolenoid = 11;
int currentSolenoid = 9;

void setup() {
  pinMode(firstSolenoid, OUTPUT);
  pinMode(secondSolenoid, OUTPUT);
  pinMode(thirdSolenoid, OUTPUT);
  Serial.setTimeout(5);
  Serial.begin(9600);
  while (! Serial);
  Serial.println("Serial tester ready.");
}

void loop() {
  // serial activities happening in the serialEvent, so nothing to do here
}

void serialEvent() {
  int totalNotes = 3;
  int pin = 0;
  int poweredPins[totalNotes] = {}; // sizing array to total pins (max possible notes played simulataneously)
  while (Serial.available()) {
    // get the new byte:
    char ch = Serial.read();
    if (ch == '0') {
      currentSolenoid = firstSolenoid;
    } else if (ch == '4') {
      currentSolenoid = secondSolenoid;
    } else if (ch == '7') {
      currentSolenoid = thirdSolenoid;
    } else {
      continue;
    }
    // power on MOSFET
    poweredPins[pin] = currentSolenoid;
    Serial.print("POWERing ");
    Serial.println(poweredPins[pin]);
    digitalWrite(currentSolenoid, HIGH);
    pin++;
  }
  // leave the MOSFET/solenoid powered
  delay(45);
  // power off all "on" MOSFETS after all notes have been played
  for (int pin = 0; pin < totalNotes; pin++) {
    if (poweredPins[pin] == 0) {
      Serial.print(poweredPins[pin]);
      Serial.println(" continuing");
      continue;
    }
    digitalWrite(poweredPins[pin], LOW);
    Serial.print(poweredPins[pin]);
    Serial.println(" Powered off");
  }
}
