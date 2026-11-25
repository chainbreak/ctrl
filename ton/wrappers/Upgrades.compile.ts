import type { CompilerConfig } from '@ton/blueprint';

export const compile: CompilerConfig = {
    lang: 'func',
    targets: [
        "contracts/stdlib.fc",
        "contracts/tap/params.fc",
        "contracts/tap/jetton-utils.fc",
        "contracts/ledger.fc",
        "contracts/upgrades.fc"
]
};
