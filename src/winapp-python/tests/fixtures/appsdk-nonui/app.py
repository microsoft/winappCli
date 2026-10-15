# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dynwinrt import RoApartment, projected_lifetime_scope
from winapp_bindings.windows.foundation import Uri

with RoApartment(1), projected_lifetime_scope():
    print(f"Host: {Uri('https://example.com/python').host}")
