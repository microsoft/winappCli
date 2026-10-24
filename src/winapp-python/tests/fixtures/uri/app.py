# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dynwinrt import RoApartment, projected_lifetime_scope
from myapp_winrt.windows.foundation import Uri

with RoApartment(1), projected_lifetime_scope():
    uri = Uri("https://example.com/python")
    print(f"Host: {uri.host}")
