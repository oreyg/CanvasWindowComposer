cmake_minimum_required(VERSION 3.15)
project(DpiHook C)

set(CMAKE_C_STANDARD 11)

add_library(DpiHook SHARED
    DpiHook.c
    minhook/hook.c
    minhook/buffer.c
    minhook/trampoline.c
    minhook/hde/hde32.c
    minhook/hde/hde64.c
)

target_include_directories(DpiHook PRIVATE minhook minhook/include)

# No default lib dependencies beyond Windows system DLLs
target_link_libraries(DpiHook PRIVATE user32 shcore)

# Output directly into the C# build output for convenience
# Output into both Debug and Release C# build directories
set_target_properties(DpiHook PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_SOURCE_DIR}/../bin/Debug/net8.0-windows"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/../bin/Release/net8.0-windows"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../bin/Debug/net8.0-windows"
)
