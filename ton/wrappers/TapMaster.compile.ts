import type { CompilerConfig } from '@ton/blueprint';

export const compile: CompilerConfig = {
    lang: 'func',
    targets: [
        "contracts/stdlib.fc",
        "contracts/tap/params.fc",
        "contracts/tap/op-codes.fc",
        "contracts/tap/discovery-params.fc",
        "contracts/tap/jetton-utils.fc",
        "contracts/tap/jetton-minter-discoverable.fc"
]
};
