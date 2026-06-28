FROM node:20-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      chromium fonts-liberation ca-certificates && rm -rf /var/lib/apt/lists/*
ENV PUPPETEER_SKIP_DOWNLOAD=1
RUN npm install -g puppeteer-core@22
ENV NODE_PATH=/usr/local/lib/node_modules
ENV CHROME=/usr/bin/chromium
