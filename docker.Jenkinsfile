
pipeline {
    parameters {
        string name: 'DOCKER_REGISTRY', defaultValue: 'git.liany.com.cn/lianyuan/', description: 'docker registry path', trim: true
    }
    agent {
        label 'dockercli && linux'
    }
    tools {
        dockerTool 'docker in path'
    }
    stages {
        stage('Build Docker Images') {
            steps {
                script {
                    env.DOCKER_REGISTRY = params.DOCKER_REGISTRY
                    env.DOCKER_TAG = env.GIT_TAG ?: env.BRANCH_NAME
                    sh 'docker compose build'
                    sh 'docker image inspect ${DOCKER_REGISTRY-}lyllmpoolweb:${DOCKER_TAG:-latest}'
                }
            }
        }
        stage('Test Startup with Migration') {
            steps {
                script {
                    def uuid = UUID.randomUUID().toString()
                    echo "Generated UUID: ${uuid}"
                    env.PROJECT_NAME="lyllmpoolweb-${uuid}"
                    sh 'bash ci/test-startup.sh'
                }
            }
        }
        stage('Publish Docker Images') {
            steps {
                script {
                    env.DOCKER_REGISTRY = params.DOCKER_REGISTRY
                    env.DOCKER_TAG = env.GIT_TAG ?: env.BRANCH_NAME
                    sh 'docker compose push'
                }
            }
        }
    }
    post {
        always {
            sh 'docker builder prune -f'
            cleanWs()
        }
    }
}
