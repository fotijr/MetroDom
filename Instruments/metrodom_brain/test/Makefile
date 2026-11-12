# Host tests for the brain's platform-free core. Any C++17 compiler: make -C Instruments/metrodom_brain/test
CXX ?= c++
CXXFLAGS ?= -std=c++17 -Wall -Wextra -Werror -Wconversion -O1 -g -fsanitize=address,undefined
SRC = ../src/brain/Brain.cpp brain_test.cpp

.PHONY: test clean
test: brain_test
	./brain_test

brain_test: $(SRC) ../src/brain/Brain.h ../bench-profile.json
	$(CXX) $(CXXFLAGS) -DBENCH_PROFILE='"../bench-profile.json"' $(SRC) -o $@

clean:
	rm -f brain_test
