#include "NativeFunctionDeclarationClassifier.h"

#include "NativeDeclarationKinds.h"
#include "NativeGraphFacts.h"
#include "NativeKindNames.h"

namespace lifeblood::native_clang
{
NativeFunctionDeclarationRole NativeFunctionDeclarationClassifier::Classify(
    const Symbol& symbol)
{
    if (!NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Function))
        return NativeFunctionDeclarationRole::None;

    return NativeGraphFacts::HasDeclarationKind(symbol, NativeDeclarationKinds::Declaration)
        ? NativeFunctionDeclarationRole::Declaration
        : NativeFunctionDeclarationRole::Definition;
}
}
