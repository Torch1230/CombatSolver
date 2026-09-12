FROM node:24-bookworm-slim
WORKDIR /home/Torch/combatsolver-presence
COPY package*.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force
COPY *.mjs ./
COPY public/ ./public/
USER 1000:1000
CMD ["node", "server.mjs"]
