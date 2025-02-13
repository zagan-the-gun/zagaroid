using MessageDelegate = Twitcher.TwitchClient.MessageDelegate;
using Permission = Twitcher.Message.Permission;

namespace Twitcher
{
    public class ChatCommand
    {
        public string Command { get; private set; }
        public Permission CommandPermission { get; private set; }
        private readonly MessageDelegate commandAction;
        private TwitchClient twitchClient;


        public ChatCommand(string command, MessageDelegate handler, Permission permission = Permission.Viewer)
        {
            Command = command;
            commandAction = handler;
            SetPermissions(permission);
        }

        public void AssignToClient(TwitchClient client)
        {
            RemoveFromClient();
            twitchClient = client;
            twitchClient?.AddCommandListener(Command, OnCommandMessageReceived);
        }

        public void RemoveFromClient()
        {
            twitchClient?.RemoveCommandListener(Command, OnCommandMessageReceived);
        }

        public void SetPermissions(Permission permissions)
        {
            CommandPermission = permissions;
        }
        
        private void OnCommandMessageReceived(Message message)
        {
            if (!PermissionsValid(message))
                return;
            
            commandAction.Invoke(message);
        }

        private bool PermissionsValid(Message message)
        {
            return (GetMessagePermission(message) & CommandPermission) != 0;
        }

        private static Permission GetMessagePermission(Message message)
        {
            Permission perm = Permission.Viewer;
            if (message.Info.admin) perm |= Permission.Admin;
            if (message.Info.moderator) perm |= Permission.Moderator;
            if (message.Info.broadcaster) perm |= Permission.Broadcaster; 
            if (message.Info.subscriber) perm |= Permission.Subscriber;
            return perm;
        }
    }
}