# Edited by: OpenAI-Codex_Responsible: Source_Code
add_library(ets2ap_input_core STATIC src/control_core.cpp)
target_include_directories(ets2ap_input_core PUBLIC include)
target_link_libraries(ets2ap_input_core PUBLIC ets2ap_test_support)

add_executable(input_core_tests tests/control_core_tests.cpp)
target_link_libraries(input_core_tests PRIVATE ets2ap_input_core ets2ap_test_support)
add_test(NAME input_core_tests COMMAND input_core_tests)

# Compile the isolated steering-curve contract assertions with the existing test target.
target_sources(input_core_tests PRIVATE tests/steering_curve_compile_tests.cpp)
# Edited by: OpenAI-Codex_Source_Code
