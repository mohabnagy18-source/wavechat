# WaveChat — Real-Time Chat with SignalR

Multi-room real-time chat built on ASP.NET Core 8 SignalR with SQL Server message history.

## Live
- API: https://wavechatmn.runasp.net/api/rooms
- Hub: wss://wavechatmn.runasp.net/hub/chat
- Frontend: (Vercel link in project description)

## Features
- Rooms, live messages over WebSockets (SignalR), persisted history (last 50 on join)
- Online user list, join/leave system messages, typing indicator
- Backend: ASP.NET Core 8, SignalR, EF Core (schema `chat`), SQL Server

## Run locally
```
cd server && dotnet run
```
