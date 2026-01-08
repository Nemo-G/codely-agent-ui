cmake_minimum_required(VERSION 3.15)
project(CodelyUnityAgentClientUIWindowSync CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Build as a shared library (DLL)
add_library(${PROJECT_NAME} SHARED
    CodelyUnityAgentClientUIWindowSync.cpp
)

# Set output directory
set_target_properties(${PROJECT_NAME} PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../../Bin/win"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../../Bin/win"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../../Bin/win"
)

# Windows-specific settings
if(WIN32)
    # Set the DLL export prefix to empty (so we get CodelyUnityAgentClientUIWindowSync.dll)
    set_target_properties(${PROJECT_NAME} PROPERTIES
        PREFIX ""
        OUTPUT_NAME "CodelyUnityAgentClientUIWindowSync"
        SUFFIX ".dll"
    )

    # Set compile flags
    target_compile_options(${PROJECT_NAME} PRIVATE
        /W4
        /O2
        /MD
    )

    # Set linker flags
    target_link_options(${PROJECT_NAME} PRIVATE
        /DLL
    )
endif()