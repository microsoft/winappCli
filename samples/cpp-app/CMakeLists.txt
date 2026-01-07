cmake_minimum_required(VERSION 3.20)
project(cpp-app)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(cpp-app main.cpp)

# Link Windows Runtime libraries
target_link_libraries(cpp-app PRIVATE WindowsApp.lib)


# Add a post-build command to apply debug identity in Debug builds
add_custom_command(TARGET cpp-app POST_BUILD
    COMMAND $<$<CONFIG:Debug>:winapp>
            $<$<CONFIG:Debug>:create-debug-identity>
            $<$<CONFIG:Debug>:$<TARGET_FILE:cpp-app>>
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
    COMMAND_EXPAND_LISTS
    COMMENT "Applying debug identity to executable..."
)