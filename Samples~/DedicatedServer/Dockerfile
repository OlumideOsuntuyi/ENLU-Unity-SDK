# Linux dedicated server image for an ENLU game-server fleet.
#
# 1. In Unity: File > Build Profiles > Linux Server (Dedicated Server), output to Build/LinuxServer/
#    and name the executable GameServer.x86_64 (change EXECUTABLE below if you use another name).
# 2. Put this Dockerfile next to the Build/ folder and run:
#      docker build -t registry.example.com/my-game/arena:1.0.0 .
#      docker push registry.example.com/my-game/arena:1.0.0
# 3. Register the workload with workload.json (see the package README).

FROM ubuntu:24.04

# ca-certificates: HTTPS to the control node (EnluServerApi). Nothing else is needed by a
# dedicated server build; add libraries here if your own plugins need them.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Never run the game server as root.
RUN useradd --create-home --uid 10001 game
WORKDIR /home/game/server

ARG EXECUTABLE=GameServer.x86_64
ENV EXECUTABLE=${EXECUTABLE}
COPY --chown=game:game Build/LinuxServer/ ./
RUN chmod +x "./${EXECUTABLE}"

USER game

# Documentation only: ENLU maps the ports declared in the workload spec and passes
# ENLU_PORT_<NAME> (listen port) and ENLU_HOST_PORT_<NAME> (public port) to the process.
EXPOSE 7777/udp

# -logFile - sends the Unity log to stdout, which `enlu logs` and the console show.
ENTRYPOINT ["/bin/sh", "-c", "exec \"./${EXECUTABLE}\" -batchmode -nographics -logFile -"]
