# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dynwinrt import RoApartment, has_package_identity, projected_lifetime_scope
from winapp_bindings.windows.application_model import Package


def main() -> int:
    with RoApartment(1), projected_lifetime_scope():
        if not has_package_identity():
            print("Not packaged")
            return 0
        package = Package.get_current()
        print(f"Package Family Name: {package.id.family_name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
