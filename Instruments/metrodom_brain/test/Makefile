# Host tests for the brain's platform-free core. Any C++17 compiler: make -C Instruments/metrodom_brain/test
CXX ?= c++
CXXFLAGS ?= -std=c++17 -Wall -Wextra -Werror -Wconversion -O1 -g -fsanitize=address,undefined
SRC = ../src/brain/Brain.cpp ../src/brain/Body.cpp ../src/brain/Devices.cpp brain_test.cpp

.PHONY: test clean
test: brain_test
	./brain_test

brain_test: $(SRC) ../src/brain/Brain.h ../src/brain/Body.h ../src/brain/Devices.h ../bench-profile.json ../bodies/m1-xylophone.hex
	$(CXX) $(CXXFLAGS) -DBENCH_PROFILE='"../bench-profile.json"' -DBODY_FIXTURE='"../bodies/m1-xylophone.hex"' $(SRC) -o $@

clean:
	rm -f brain_test
