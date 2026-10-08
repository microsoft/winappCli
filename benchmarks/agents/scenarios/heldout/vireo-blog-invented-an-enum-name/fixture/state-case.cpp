#include <winrt/Windows.ApplicationModel.h>
using winrt::Windows::ApplicationModel::StartupTaskState;
bool DisabledByPerson(StartupTaskState state) {
    return state == StartupTaskState::BlockedByUser;
}
