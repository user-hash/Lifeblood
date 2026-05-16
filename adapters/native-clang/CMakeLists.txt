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
    src/ClangBuildArgumentFactsCollector.cpp
    src/ClangBuildArgumentFactsCollector.h
    src/ClangCommandLineMacroCollector.cpp
    src/ClangCommandLineMacroCollector.h
    src/ClangCompilationDatabase.cpp
    src/ClangCompilationDatabase.h
    src/ClangCompileCommandReader.cpp
    src/ClangCompileCommandReader.h
    src/ClangIndex.cpp
    src/ClangIndex.h
    src/ClangParseArgumentBuilder.cpp
    src/ClangParseArgumentBuilder.h
    src/ClangSourceMapper.cpp
    src/ClangSourceMapper.h
    src/ClangTranslationUnitParser.cpp
    src/ClangTranslationUnitParser.h
    src/ClangUtilities.cpp
    src/ClangUtilities.h
    src/GraphModel.h
    src/JsonGraphElements.cpp
    src/JsonGraphElements.h
    src/JsonGraphWriter.cpp
    src/JsonGraphWriter.h
    src/LibClangExtractor.cpp
    src/LibClangExtractor.h
    src/NativeAstVisitor.cpp
    src/NativeAstVisitor.h
    src/NativeCallKinds.h
    src/NativeCountPropertyWriter.h
    src/NativeCompileCommand.h
    src/NativeEdgeClassification.cpp
    src/NativeEdgeClassification.h
    src/NativeEdgeMetricClassification.cpp
    src/NativeEdgeMetricClassification.h
    src/NativeEdgeRole.cpp
    src/NativeEdgeRole.h
    src/NativeDeclarationKinds.h
    src/NativeGraphBuilder.cpp
    src/NativeGraphBuilder.h
    src/NativeGraphSink.h
    src/NativeEvidenceKinds.h
    src/NativeIncludeEmitter.cpp
    src/NativeIncludeEmitter.h
    src/NativeKindInventory.cpp
    src/NativeKindInventory.h
    src/NativeKindNames.h
    src/NativeLinkageNames.h
    src/NativeMacroEmitter.cpp
    src/NativeMacroEmitter.h
    src/NativeMacroSources.h
    src/NativeDeclarationEmitter.cpp
    src/NativeDeclarationEmitter.h
    src/NativeDiagnosticSummary.h
    src/NativeDeclaredSurfaceInventory.cpp
    src/NativeDeclaredSurfaceInventory.h
    src/NativeDirectionalSymbolCounts.cpp
    src/NativeDirectionalSymbolCounts.h
    src/NativeExtractionSession.cpp
    src/NativeExtractionSession.h
    src/NativeFunctionEmitter.cpp
    src/NativeFunctionEmitter.h
    src/NativeFunctionDeclarationClassifier.cpp
    src/NativeFunctionDeclarationClassifier.h
    src/NativeFileRegistry.cpp
    src/NativeFileRegistry.h
    src/NativeFileCallSymbolMetrics.cpp
    src/NativeFileCallSymbolMetrics.h
    src/NativeFileGraphMetrics.cpp
    src/NativeFileGraphMetrics.h
    src/NativeGlobalEmitter.cpp
    src/NativeGlobalEmitter.h
    src/NativeGraphFinalizer.cpp
    src/NativeGraphFinalizer.h
    src/NativeGraphFacts.cpp
    src/NativeGraphFacts.h
    src/NativeGraphMetricPropertyKeys.h
    src/NativeGraphPropertyKeys.h
    src/NativeGraphOwnershipIndex.cpp
    src/NativeGraphOwnershipIndex.h
    src/NativeModuleBuildSummary.cpp
    src/NativeModuleBuildSummary.h
    src/NativeModuleTracker.cpp
    src/NativeModuleTracker.h
    src/NativeModuleGraphMetrics.cpp
    src/NativeModuleGraphMetrics.h
    src/NativePreprocessorEmitter.cpp
    src/NativePreprocessorEmitter.h
    src/NativeParseStatuses.h
    src/NativePropertyWriter.cpp
    src/NativePropertyWriter.h
    src/NativeReferenceEdgeWriter.cpp
    src/NativeReferenceEdgeWriter.h
    src/NativeReferenceEmitter.cpp
    src/NativeReferenceEmitter.h
    src/NativeReferenceKinds.h
    src/NativeReferenceMetrics.cpp
    src/NativeReferenceMetrics.h
    src/NativeSymbolIds.cpp
    src/NativeSymbolIds.h
    src/NativeTypeEmitter.cpp
    src/NativeTypeEmitter.h
    src/NativeTypeMemberEmitter.cpp
    src/NativeTypeMemberEmitter.h
    src/NativeVisibilityCounts.cpp
    src/NativeVisibilityCounts.h
    src/NativeVisibilityNames.h
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
