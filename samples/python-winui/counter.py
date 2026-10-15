# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dataclasses import dataclass


@dataclass
class Counter:
    value: int = 0

    def increment(self) -> None:
        self.value += 1

    def decrement(self) -> None:
        if self.value > 0:
            self.value -= 1
