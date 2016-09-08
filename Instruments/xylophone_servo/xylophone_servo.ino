#include <Servo.h>

Servo spinServo;
Servo strikeServo;
String currentPosition = "";
String lastPosition = "";
int strikeStart = 12;

void setup() {
  spinServo.attach(9);  // attaches the servo on pin 9 to the servo object
  strikeServo.attach(10);
  spin(90);
  strikeServo.write(strikeStart);
  Serial.begin(9600);
  while (! Serial); // Wait untilSerial is ready - Leonardo
  Serial.write("Xylo ready.");
}

void spin(int newPosition) {
  spinServo.write(newPosition);
  lastPosition = newPosition;
  currentPosition = "";
}

void loop() {
  if (Serial.available())
  {
    char ch = Serial.read();

    switch (ch) {
      case '0'...'9':
        currentPosition += ch;
        break;
      case 'p':
        strikeServo.write(16);
        delay(75);
        strikeServo.write(strikeStart);
        break;
      case 'm':
        spin(currentPosition.toInt());
        break;
      case '?':
        Serial.write("Xylophone | Servo");
        currentPosition = "";
        break;
    }
  }
}

