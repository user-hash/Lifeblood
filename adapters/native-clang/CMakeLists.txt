cmake_minimum_required(VERSION 3.20)

project(LifebloodNativeClang LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(LLVM_ROOT "C:/Program Files/LLVM" CACHE PATH "LLVM installation root")
set(LIBCLANG_INCLUDE_DIR "${LLVM_ROOT}/include" CACHE PATH "libclang include directory")
set(LIBCLANG_LIBRARY "${LLVM_ROOT}/lib/libclang.lib" CACHE FILEPATH "libclang import library")
set(LIBCLANG_RUNTIME "${LLVM_ROOT}/bin/libclang.dll" CACHE FILEPATH "libclang runtime DLL")

if (NOT EXISTS "${LIBCLANG_INCLUDE_DIR}/clang-c/Index.h")
    message(FATAL_ERROR "libclang C API headers not found under ${LIBCLANG_INCLUDE_DIR}")
endif()

if (NOT EXISTS "${LIBCLANG_LIBRARY}")
    message(FATAL_ERROR "libclang import library not found at ${LIBCLANG_LIBRARY}")
endif()

add_executable(lifeblood-native-clang
    src/ClangUtilities.cpp
    src/ClangUtilities.h
    src/GraphModel.h
    src/JsonGraphWriter.cpp
    src/JsonGraphWriter.h
    src/LibClangExtractor.cpp
    src/LibClangExtractor.h
    src/NativeGraphBuilder.cpp
    src/NativeGraphBuilder.h
    src/NativeGraphSink.h
    src/NativeSymbolIds.cpp
    src/NativeSymbolIds.h
    src/Options.h
    src/main.cpp
)

target_include_directories(lifeblood-native-clang PRIVATE
    "${LIBCLANG_INCLUDE_DIR}"
)

target_link_libraries(lifeblood-native-clang PRIVATE
    "${LIBCLANG_LIBRARY}"
)

if (EXISTS "${LIBCLANG_RUNTIME}")
    add_custom_command(TARGET lifeblood-native-clang POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "${LIBCLANG_RUNTIME}"
            "$<TARGET_FILE_DIR:lifeblood-native-clang>"
    )
endif()
