import type { CompilerConfig } from '@ton/blueprint';

export const compile: CompilerConfig = {
    lang: 'func',
    targets: [
        "contracts/stdlib.fc",
        "contracts/nft/params.fc",
        "contracts/nft/op-codes.fc",
        "contracts/nft/nft-item.fc",
        "contracts/nft/resolver.fc"
]
};
